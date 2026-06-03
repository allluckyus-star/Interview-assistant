window.IaFileImport = (function () {
  function readTextFile(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result ?? ""));
      reader.onerror = () => reject(new Error("Could not read text file."));
      reader.readAsText(file);
    });
  }

  function readBase64(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const dataUrl = String(reader.result ?? "");
        const comma = dataUrl.indexOf(",");
        resolve(comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl);
      };
      reader.onerror = () => reject(new Error("Could not read file."));
      reader.readAsDataURL(file);
    });
  }

  function isTxtFile(file) {
    const name = (file.name || "").toLowerCase();
    return name.endsWith(".txt") || file.type === "text/plain";
  }

  async function extractText(file) {
    if (!file) throw new Error("No file selected.");
    if (isTxtFile(file)) return readTextFile(file);

    const b64 = await readBase64(file);
    const r = await IaApi.post("/context/extract-text", {
      file_name: file.name || "document",
      content_base64: b64,
    });
    if (!r.ok) throw new Error(r.error || "Companion not running.");
    if (!r.data?.ok) throw new Error(r.data?.error || "Could not extract text.");
    return r.data.text ?? "";
  }

  return { extractText };
})();
