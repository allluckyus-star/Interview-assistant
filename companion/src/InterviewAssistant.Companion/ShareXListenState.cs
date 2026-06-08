namespace InterviewAssistant.Companion;

internal sealed class ShareXListenState
{
    private readonly object _gate = new();
    private bool _imageEnabled;
    private bool _textEnabled;

    public bool ImageEnabled
    {
        get { lock (_gate) return _imageEnabled; }
    }

    public bool TextEnabled
    {
        get { lock (_gate) return _textEnabled; }
    }

    public (bool Image, bool Text) Snapshot()
    {
        lock (_gate) return (_imageEnabled, _textEnabled);
    }

    public (bool Image, bool Text) Update(bool? imageEnabled, bool? textEnabled)
    {
        lock (_gate)
        {
            if (imageEnabled.HasValue) _imageEnabled = imageEnabled.Value;
            if (textEnabled.HasValue) _textEnabled = textEnabled.Value;
            return (_imageEnabled, _textEnabled);
        }
    }
}

internal sealed record ShareXShortcutBinding(
    bool Ctrl,
    bool Alt,
    bool Shift,
    int KeyVk,
    int[]? AltKeyVks = null)
{
    public int[] EffectiveKeyVks =>
        AltKeyVks is { Length: > 0 }
            ? AltKeyVks.Prepend(KeyVk).Where(v => v > 0).Distinct().ToArray()
            : KeyVk > 0 ? [KeyVk] : [];
}
