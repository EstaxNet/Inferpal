// What the chat reads of a file dropped from the system's file manager: one value for the webview, which never reads a
// larger file into the page, and for the extension, which says it is not attached.

/** The largest dropped file that is read, in bytes: the documents' own limit (20 MB). */
export const DROPPED_FILE_LIMIT = 20 * 1024 * 1024;
