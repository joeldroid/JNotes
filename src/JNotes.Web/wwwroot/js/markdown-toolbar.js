// Textarea manipulation for MarkdownToolbar.razor. Kept as plain DOM operations (not Blazor JS
// interop return values) — every function ends by dispatching a real "input" event, which
// Blazor's own event delegation picks up exactly like a keystroke, so the existing @oninput
// binding on the textarea (and therefore the Body/BodyChanged two-way bind) updates normally
// with no separate C#-side plumbing needed.

function fireInput(textarea) {
    textarea.focus();
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
}

export function wrapSelection(textarea, before, after, placeholder) {
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const value = textarea.value;
    const selected = value.slice(start, end) || placeholder;

    textarea.value = value.slice(0, start) + before + selected + after + value.slice(end);

    const selStart = start + before.length;
    textarea.setSelectionRange(selStart, selStart + selected.length);
    fireInput(textarea);
}

export function insertAtCursor(textarea, text) {
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const value = textarea.value;

    textarea.value = value.slice(0, start) + text + value.slice(end);

    const pos = start + text.length;
    textarea.setSelectionRange(pos, pos);
    fireInput(textarea);
}

// Inserts `prefix + placeholder` as its own line below the line the cursor is on (or in place of the
// cursor's line if that one is blank), then selects the placeholder so typing replaces it. With
// blankBefore, a blank line is kept above it (for a checklist category heading). Used by the
// checklist editor's "Add item" / "Add category" buttons.
export function insertLine(textarea, prefix, placeholder, blankBefore) {
    const value = textarea.value;
    const pos = textarea.selectionEnd;

    const lineStart = value.lastIndexOf('\n', pos - 1) + 1;
    let lineEnd = value.indexOf('\n', pos);
    if (lineEnd === -1) {
        lineEnd = value.length;
    }

    const currentLineBlank = value.slice(lineStart, lineEnd).trim() === '';
    const insertAt = currentLineBlank ? lineStart : lineEnd;
    const before = value.slice(0, insertAt);

    let lead = '';
    if (before.length > 0) {
        const wanted = blankBefore ? 2 : 1;
        const existing = before.length - before.replace(/\n+$/, '').length;
        lead = '\n'.repeat(Math.max(0, wanted - existing));
    }

    textarea.value = before + lead + prefix + placeholder + value.slice(lineEnd);

    const selStart = insertAt + lead.length + prefix.length;
    textarea.setSelectionRange(selStart, selStart + placeholder.length);
    fireInput(textarea);
}

// Toggles a line prefix (list marker) across every selected line. If every non-blank selected
// line already has the prefix, it's removed from all of them; otherwise it's added to all of
// them. addPrefix/hasPrefix/stripPrefix let bullet and ordered lists share this one routine.
function toggleLinePrefix(textarea, addPrefix, hasPrefix, stripPrefix) {
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const value = textarea.value;

    const lineStart = value.lastIndexOf('\n', start - 1) + 1;
    const searchFrom = end > start ? end - 1 : end;
    let lineEnd = value.indexOf('\n', searchFrom);
    if (lineEnd === -1) {
        lineEnd = value.length;
    }

    const lines = value.slice(lineStart, lineEnd).split('\n');
    const nonBlank = lines.filter(l => l.trim() !== '');
    const allPrefixed = nonBlank.length > 0 && nonBlank.every(hasPrefix);

    const newLines = lines.map(l => (l.trim() === '' ? l : (allPrefixed ? stripPrefix(l) : addPrefix(l))));
    const newBlock = newLines.join('\n');

    textarea.value = value.slice(0, lineStart) + newBlock + value.slice(lineEnd);
    textarea.setSelectionRange(lineStart, lineStart + newBlock.length);
    fireInput(textarea);
}

export function toggleBulletList(textarea) {
    toggleLinePrefix(textarea, l => '- ' + l, l => /^-\s/.test(l), l => l.replace(/^-\s/, ''));
}

export function toggleOrderedList(textarea) {
    // Every inserted line uses "1. " — CommonMark renders an ordered list's numbering from the
    // first item regardless of the literal digits used for later items, so this still displays
    // as 1, 2, 3, … without this code needing to track a running count.
    toggleLinePrefix(textarea, l => '1. ' + l, l => /^\d+\.\s/.test(l), l => l.replace(/^\d+\.\s/, ''));
}
