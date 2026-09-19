// Pasting an image into TextNoteEditor's textarea. Loaded by TextNoteEditor.razor, which owns the
// upload; this only spots the paste and hands the image file over.
//
// The file goes to .NET as a JS stream reference (DotNet.createJSStreamReference), not a byte
// array: a screenshot is far bigger than the 32 KB SignalR message limit that plain JS interop
// arguments are subject to.

export function attach(textarea, dotNetRef) {
    textarea.addEventListener('paste', event => {
        const clipboard = event.clipboardData;
        if (!clipboard) {
            return;
        }

        const file = Array.from(clipboard.files).find(f => f.type.startsWith('image/'));

        // Only take over a paste that is *just* an image (a screenshot, or "Copy image" in a
        // browser). If there's text alongside it — copying from Word or a web page often puts a
        // rendered image next to the text — leave it to paste normally.
        if (!file || clipboard.getData('text/plain') !== '') {
            return;
        }

        event.preventDefault();
        dotNetRef.invokeMethodAsync('OnImagePastedAsync', DotNet.createJSStreamReference(file), file.name, file.size);
    });
}
