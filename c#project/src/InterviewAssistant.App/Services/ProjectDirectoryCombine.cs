using System.IO;
using System.Text;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Walks a directory tree, collects text/code files (many languages), skips dependency/build folders,
/// and builds one document suitable for pasting or exporting (does not auto-send).
/// </summary>
public static class ProjectDirectoryCombine
{
    private const string SeparatorLine = "================================================================================";

    /// <summary>Directory name segments; any matching part in a relative path skips that subtree when walking.</summary>
    public static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".bzr",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".tox", ".nox", "venv", ".venv", "env", ".env",
        "node_modules", "bower_components", "jspm_packages",
        ".idea", ".vscode", ".vs",
        "dist", "build", "out", "target", "bin", "obj",
        "packages", ".nuget", "TestResults", "coverage", "htmlcov",
        "vendor", "Pods", "Carthage", "DerivedData",
        ".gradle", ".terraform", ".next", ".nuxt", ".output", ".parcel-cache",
        "site-packages", ".cache", "__MACOSX",
        ".git-worktrees",
    };

    public static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "combined_output.txt",
    };

    /// <summary>Extensions of files to include (lowercase, with dot).</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Script / web
        ".py", ".pyw", ".pyi", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less",
        ".json", ".jsonc", ".yaml", ".yml",
        ".md", ".mdx", ".txt", ".rst", ".adoc",
        ".env", ".toml", ".ini", ".cfg", ".editorconfig",
        ".sql", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        // JVM / Android
        ".java", ".kt", ".kts", ".gradle",
        // Systems / native
        ".go", ".rs", ".zig",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh", ".hxx",
        ".cs", ".fs", ".fsx", ".vb",
        ".swift", ".m", ".mm",
        // Other languages
        ".rb", ".rake", ".gemspec",
        ".php", ".phtml",
        ".pl", ".pm",
        ".lua", ".vim",
        ".dart", ".ex", ".exs", ".erl", ".hrl",
        ".hs", ".lhs", ".scala", ".sc",
        ".clj", ".cljs", ".cljc", ".edn", ".rkt",
        // Config / markup
        ".xml", ".xaml", ".axml", ".plist",
        ".cmake", ".proto", ".graphql", ".gql",
        // Docs as code
        ".graphqls",
        ".patch", ".diff",
        // .NET / tooling
        ".props", ".targets", ".config", ".nuspec", ".ruleset",
        // Wasm / low-level
        ".wat",
    };

    public static readonly HashSet<string> SpecialFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "Rakefile", "Gemfile", "Podfile",
        "Vagrantfile", "Jenkinsfile", "Brewfile",
        ".gitignore", ".gitattributes", "docker-compose.yml", "docker-compose.yaml",
        ".dockerignore", ".npmrc", ".yarnrc", ".nvmrc", "Cargo.toml", "Cargo.lock",
        "go.mod", "go.sum", "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle",
        "CMakeLists.txt", "Package.swift", "mix.exs", "rebar.config",
        "webpack.config.js", "vite.config.ts", "rollup.config.js", "angular.json",
        "tsconfig.json", "jsconfig.json",
    };

    public sealed record Result(string Text, int FilesIncluded, bool Truncated, string? TruncateNote);

    /// <summary>Written snapshot on disk for paste / attach / manual upload flows.</summary>
    public sealed record FileExportResult(string FilePath, int FilesIncluded, bool Truncated, string? TruncateNote);

    public static Result Combine(
        string rootDirectory,
        int maxTotalCharacters = 400_000,
        int maxFiles = 1_000)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Root path does not exist: {root}");

        List<string> files;
        try
        {
            files = CollectTextFilePaths(root);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result("", 0, false, "Access denied for part of the directory.");
        }

        var sb = new StringBuilder(capacity: Math.Min(maxTotalCharacters + 2048, 512_000));
        WritePrologue(sb, root);

        var included = 0;
        var truncated = false;
        string? truncateNote = null;

        foreach (var filePath in files)
        {
            if (included >= maxFiles)
            {
                truncated = true;
                truncateNote = $"Stopped after {maxFiles} files (limit).";
                break;
            }

            try
            {
                var rel = Path.GetRelativePath(root, filePath);
                var displayPath = "./" + rel.Replace('\\', '/');

                var blockHeader = $"{Environment.NewLine}{Environment.NewLine}{SeparatorLine}{Environment.NewLine}{displayPath}{Environment.NewLine}{SeparatorLine}{Environment.NewLine}{Environment.NewLine}";
                if (sb.Length + blockHeader.Length > maxTotalCharacters)
                {
                    truncated = true;
                    truncateNote = $"Stopped: combined text would exceed ~{maxTotalCharacters} characters.";
                    break;
                }

                sb.Append(blockHeader);

                string content;
                try
                {
                    content = File.ReadAllText(filePath, new UTF8Encoding(false, true));
                }
                catch (DecoderFallbackException)
                {
                    content = "[Skipped: could not decode as UTF-8]\n";
                }
                catch (Exception ex)
                {
                    content = $"[Error reading file: {ex.Message}]\n";
                }

                if (sb.Length + content.Length > maxTotalCharacters)
                {
                    var room = maxTotalCharacters - sb.Length;
                    if (room > 64)
                    {
                        sb.Append(content.AsSpan(0, room));
                        sb.AppendLine();
                    }
                    sb.AppendLine($"[…truncated: file was larger than remaining character budget]");
                    truncated = true;
                    truncateNote ??= $"Output truncated at ~{maxTotalCharacters} characters.";
                    break;
                }

                sb.Append(content);
                included++;
            }
            catch
            {
                // skip file
            }
        }

        if (included == 0 && !truncated)
            return new Result("", 0, false, null);

        return new Result(sb.ToString(), included, truncated, truncateNote);
    }

    /// <summary>
    /// Streams a full snapshot to <paramref name="outputFilePath"/> with a high byte/file cap (for attach or manual upload).
    /// </summary>
    public static FileExportResult ExportCombinedToFile(
        string rootDirectory,
        string outputFilePath,
        long maxTotalUtf8Bytes = 50_000_000,
        int maxFiles = 5_000)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Root path does not exist: {root}");

        var outFull = Path.GetFullPath(outputFilePath);
        var parentDir = Path.GetDirectoryName(outFull);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        List<string> files;
        try
        {
            files = CollectTextFilePaths(root);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileExportResult(outFull, 0, false, "Access denied for part of the directory.");
        }

        var truncated = false;
        string? truncateNote = null;
        var included = 0;
        var utf8 = new UTF8Encoding(false);
        var newLineBytes = utf8.GetBytes(Environment.NewLine);
        var hardStop = false;

        using (var fs = new FileStream(outFull, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            void WriteBytes(ReadOnlySpan<byte> bytes)
            {
                fs.Write(bytes);
            }

            WriteBytes(utf8.GetBytes("Project snapshot — combined text/code files under this folder:"));
            WriteBytes(newLineBytes);
            WriteBytes(utf8.GetBytes("Root: " + root.Replace('\\', '/')));
            WriteBytes(newLineBytes);

            foreach (var filePath in files)
            {
                if (hardStop)
                    break;

                if (included >= maxFiles)
                {
                    truncated = true;
                    truncateNote = $"Stopped after {maxFiles} files (limit).";
                    break;
                }

                try
                {
                    var rel = Path.GetRelativePath(root, filePath);
                    var displayPath = "./" + rel.Replace('\\', '/');
                    var headerText =
                        $"{Environment.NewLine}{Environment.NewLine}{SeparatorLine}{Environment.NewLine}{displayPath}{Environment.NewLine}{SeparatorLine}{Environment.NewLine}{Environment.NewLine}";
                    var headerBytes = utf8.GetBytes(headerText);
                    if (fs.Length + headerBytes.Length > maxTotalUtf8Bytes)
                    {
                        truncated = true;
                        truncateNote = $"Stopped: snapshot would exceed ~{maxTotalUtf8Bytes} UTF-8 bytes.";
                        break;
                    }

                    WriteBytes(headerBytes);

                    try
                    {
                        using var reader = new StreamReader(
                            filePath,
                            new UTF8Encoding(false, true),
                            detectEncodingFromByteOrderMarks: false);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var lineBytes = utf8.GetBytes(line);
                            if (fs.Length + lineBytes.Length + newLineBytes.Length > maxTotalUtf8Bytes)
                            {
                                var note =
                                    utf8.GetBytes("[…truncated: byte budget reached; remaining content omitted]" + Environment.NewLine);
                                if (fs.Length + note.Length <= maxTotalUtf8Bytes)
                                    WriteBytes(note);
                                truncated = true;
                                truncateNote ??= $"Output truncated at ~{maxTotalUtf8Bytes} UTF-8 bytes.";
                                hardStop = true;
                                break;
                            }

                            WriteBytes(lineBytes);
                            WriteBytes(newLineBytes);
                        }
                    }
                    catch (DecoderFallbackException)
                    {
                        var skipLine = utf8.GetBytes("[Skipped: could not decode as UTF-8]" + Environment.NewLine);
                        if (fs.Length + skipLine.Length > maxTotalUtf8Bytes)
                        {
                            truncated = true;
                            truncateNote ??= $"Output truncated at ~{maxTotalUtf8Bytes} UTF-8 bytes.";
                            hardStop = true;
                        }
                        else
                        {
                            WriteBytes(skipLine);
                        }
                    }
                    catch (Exception ex)
                    {
                        var err = utf8.GetBytes($"[Error reading file: {ex.Message}]" + Environment.NewLine);
                        if (fs.Length + err.Length > maxTotalUtf8Bytes)
                        {
                            truncated = true;
                            truncateNote ??= $"Output truncated at ~{maxTotalUtf8Bytes} UTF-8 bytes.";
                            hardStop = true;
                        }
                        else
                        {
                            WriteBytes(err);
                        }
                    }

                    included++;
                }
                catch
                {
                    // skip file
                }
            }
        }

        if (included == 0 && !truncated)
        {
            try
            {
                File.Delete(outFull);
            }
            catch
            {
                // ignore
            }

            return new FileExportResult(outFull, 0, false, null);
        }

        return new FileExportResult(outFull, included, truncated, truncateNote);
    }

    /// <summary>Collects sorted absolute paths to included files under <paramref name="rootDirectory"/>.</summary>
    public static List<string> CollectTextFilePaths(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Root path does not exist: {root}");

        var outputCombinedPath = Path.Combine(root, "combined_output.txt");
        var outputCombinedFull = Path.GetFullPath(outputCombinedPath);

        var files = new List<string>(512);
        foreach (var path in Directory.EnumerateFiles(root, "*.*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.System,
                 }))
        {
            try
            {
                var rel = Path.GetRelativePath(root, path);
                if (ShouldSkipRelativePath(rel))
                    continue;

                if (string.Equals(Path.GetFullPath(path), outputCombinedFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(path);
                if (name.Length > 0 && SkipFileNames.Contains(name))
                    continue;

                if (!IsWantedFile(path, name))
                    continue;

                files.Add(path);
            }
            catch
            {
                // skip entry
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static void WritePrologue(StringBuilder sb, string root)
    {
        sb.AppendLine("This is my project. It contains the following files:");
        sb.Append("Root: ");
        sb.AppendLine(root.Replace('\\', '/'));
    }

    private static bool ShouldSkipRelativePath(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
                continue;
            if (SkipDirectoryNames.Contains(part))
                return true;
        }

        return false;
    }

    private static bool IsWantedFile(string fullPath, string fileName)
    {
        if (SpecialFileNames.Contains(fileName))
            return true;

        if (SkipFileNames.Contains(fileName))
            return false;

        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".min.js", StringComparison.Ordinal) || lower.EndsWith(".min.css", StringComparison.Ordinal))
            return false;
        if (lower.EndsWith(".bundle.js", StringComparison.Ordinal) || lower.EndsWith(".bundle.css", StringComparison.Ordinal))
            return false;
        if (lower.EndsWith(".map", StringComparison.Ordinal))
            return false;

        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0)
            return false;

        return TextExtensions.Contains(ext);
    }
}
