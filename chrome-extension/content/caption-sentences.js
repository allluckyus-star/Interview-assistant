/** Split live caption text into sentence boxes for the feed UI. */
window.IaCaptionSentences = (function () {
  /** Western + CJK sentence terminators (e.g. 。！？). */
  const SENTENCE_END_RE = /[.!?…。！？]/;

  function splitIntoSentences(text) {
    const t = (text || "").trim();
    if (!t) return [];

    const parts = [];
    let buf = "";

    for (let i = 0; i < t.length; i++) {
      buf += t[i];
      if (!SENTENCE_END_RE.test(t[i])) continue;

      while (i + 1 < t.length && /\s/.test(t[i + 1])) {
        i += 1;
        buf += t[i];
      }

      const sent = buf.trim();
      if (sent) parts.push(sent);
      buf = "";
    }

    const tail = buf.trim();
    if (tail) parts.push(tail);

    return parts.length ? parts : [t];
  }

  /** Returns [{ text, start, end }] with character offsets in the original string. */
  function splitWithRanges(text) {
    const src = text || "";
    const t = src.trim();
    if (!t) return [];

    const sentences = splitIntoSentences(t);
    const items = [];
    let pos = 0;

    for (const sent of sentences) {
      let start = src.indexOf(sent, pos);
      if (start < 0) start = pos;
      const end = start + sent.length;
      items.push({ text: sent, start, end });
      pos = end;
    }

    return items;
  }

  function joinSentences(sentences) {
    return (sentences || [])
      .map((s) => (s || "").trim())
      .filter(Boolean)
      .join(" ");
  }

  return { splitIntoSentences, splitWithRanges, joinSentences };
})();
