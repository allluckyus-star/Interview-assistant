/** Split live caption text into sentence boxes for the feed UI. */
window.IaCaptionSentences = (function () {
  function splitIntoSentences(text) {
    const t = (text || "").trim();
    if (!t) return [];
    const parts = t
      .split(/(?<=[.!?…])\s+/)
      .map((s) => s.trim())
      .filter(Boolean);
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

  /** Character index where the live-sync tail begins (~20 sentences before edge; not the green zone). */
  function pendingStartIndex(full, windowSize) {
    const items = splitWithRanges(full);
    if (!items.length || items.length <= windowSize) return 0;
    return items[items.length - windowSize].start;
  }

  return { splitIntoSentences, splitWithRanges, joinSentences, pendingStartIndex };
})();
