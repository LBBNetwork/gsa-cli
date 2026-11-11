using System;

namespace Xml2
{
    /// <summary>
    /// A read-only, point-in-time copy of an <see cref="XmlSettings"/>
    /// document, obtained through <see cref="XmlSettings.Snapshot"/>.
    ///
    /// The snapshot is fully independent of the live document: writers
    /// may keep mutating the instance it was taken from, and every read
    /// on this snapshot keeps returning the state that existed at the
    /// moment the copy was taken. Reads on the snapshot do not touch the
    /// live document, so they never block on, or are blocked by,
    /// concurrent writers.
    ///
    /// The surface is the READ half of XmlSettings only - scalar reads,
    /// attribute reads, record-set inspection, and section queries.
    /// There are no write and no lifecycle methods: the snapshot is
    /// immutable by design and needs no saving.
    ///
    /// Path rules and exception behavior are IDENTICAL to the live
    /// instance - every method validates <c>null</c> and malformed paths
    /// with the same exception types and messages, so code written
    /// against the live surface reads the same way here.
    /// </summary>
    public sealed class XmlSettingsSnapshot
    {
        /// <summary>
        /// The snapshot wraps its deep-copied root in a private
        /// XmlSettings instance - never exposed - and delegates all
        /// reads to it. This keeps one implementation of path
        /// resolution, argument rules and error messages shared by the
        /// live and snapshot surfaces, and any future read method on
        /// XmlSettings is available here automatically. The wrapper's
        /// own monitor is effectively uncontended (one owner: this
        /// snapshot), which is the documented price of that uniformity.
        /// </summary>
        private readonly XmlSettings settings;

        /// <summary>
        /// Creates a snapshot around an already-deep-copied root. The
        /// caller owns the tree from that point on.
        /// </summary>
        /// <param name="root">
        /// The deep-copied document root; never null - see
        /// <see cref="XmlSettings.Snapshot"/>.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="root"/> is null (the live document was empty
        /// when the snapshot was taken).
        /// </exception>
        internal XmlSettingsSnapshot(XmlTreeNode root)
        {
            if (root == null)
            {
                throw new InvalidOperationException(
                    "Cannot snapshot an empty document: there is no root element to copy.");
            }

            this.settings = new XmlSettings(root);
        }

        // ------------------------------------------------------------------
        // Scalar reads
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the text content of the element at
        /// <paramref name="path"/>, as captured when the snapshot was
        /// taken.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative (same rules as the live instance).</param>
        /// <returns>
        /// The element's text content at snapshot time; null if the
        /// element did not exist (same null/empty distinction as
        /// <see cref="XmlSettings.GetValue"/>).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public string GetValue(string path)
        {
            return this.settings.GetValue(path);
        }

        /// <summary>
        /// Reads a scalar as in <see cref="GetValue"/>, reporting
        /// presence through the out argument.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="value">
        /// The element's text content (empty string if it was stored
        /// empty), or null if the element does not exist.
        /// </param>
        /// <returns>true if the element exists, false if not.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public bool TryGetValue(string path, out string value)
        {
            return this.settings.TryGetValue(path, out value);
        }

        // ------------------------------------------------------------------
        // Attribute reads
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the attribute <paramref name="attrName"/> on the element
        /// at <paramref name="path"/>, as captured when the snapshot was
        /// taken.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name (well-formed XML name; case-sensitive).</param>
        /// <returns>
        /// The attribute's value (which may be empty string), or null if
        /// the element or the attribute does not exist.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="attrName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad segment, or <paramref name="attrName"/> is empty or not a well-formed XML name.</exception>
        public string GetAttribute(string path, string attrName)
        {
            return this.settings.GetAttribute(path, attrName);
        }

        /// <summary>
        /// Reads an attribute as in <see cref="GetAttribute"/>,
        /// reporting presence through the out argument.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name (well-formed XML name; case-sensitive).</param>
        /// <param name="value">
        /// The attribute's value (empty string if it was stored empty),
        /// or null if the element or the attribute does not exist.
        /// </param>
        /// <returns>true if the element carries the attribute, false if not.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="attrName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad segment, or <paramref name="attrName"/> is empty or not a well-formed XML name.</exception>
        public bool TryGetAttribute(string path, string attrName, out string value)
        {
            return this.settings.TryGetAttribute(path, attrName, out value);
        }

        // ------------------------------------------------------------------
        // Record-set inspection (repeated elements)
        // ------------------------------------------------------------------

        /// <summary>
        /// The number of occurrences of the repeated element named by
        /// <paramref name="path"/>, as captured when the snapshot was
        /// taken.
        /// </summary>
        /// <param name="path">Record path, root-relative (the last segment names the repeated element).</param>
        /// <returns>
        /// The occurrence count at snapshot time - 0 if the container or
        /// the records do not exist.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public int Count(string path)
        {
            return this.settings.Count(path);
        }

        /// <summary>
        /// Finds the first record at <paramref name="path"/> whose
        /// attribute <paramref name="attrName"/> equals
        /// <paramref name="attrValue"/>.
        /// </summary>
        /// <param name="path">Record path, root-relative.</param>
        /// <param name="attrName">Attribute name to match (case-sensitive).</param>
        /// <param name="attrValue">Attribute value to match; null is treated as empty string.</param>
        /// <returns>The 0-based occurrence index of the first match, or -1 if no record matches.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="attrName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad segment, or <paramref name="attrName"/> is empty or not a well-formed XML name.</exception>
        public int FindByAttribute(string path, string attrName, string attrValue)
        {
            return this.settings.FindByAttribute(path, attrName, attrValue);
        }

        /// <summary>
        /// Reads the text content of the record at
        /// <paramref name="index"/> within the set at
        /// <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Record path, root-relative.</param>
        /// <param name="index">0-based occurrence index of the record.</param>
        /// <param name="value">
        /// The record's text content (empty string if it was stored
        /// empty), or null if the index is out of range.
        /// </param>
        /// <returns>true if the index is within the set, false otherwise.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public bool TryGetRecordValue(string path, int index, out string value)
        {
            return this.settings.TryGetRecordValue(path, index, out value);
        }

        /// <summary>
        /// Reads the attribute <paramref name="attrName"/> of the record
        /// at <paramref name="index"/> within the set at
        /// <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Record path, root-relative.</param>
        /// <param name="index">0-based occurrence index of the record.</param>
        /// <param name="attrName">Attribute name to read (case-sensitive).</param>
        /// <param name="value">
        /// The attribute's value (empty string if it was stored empty),
        /// or null if the index is out of range or the record does not
        /// carry the attribute.
        /// </param>
        /// <returns>true if the record exists and carries the attribute, false otherwise.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="attrName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad segment, or <paramref name="attrName"/> is empty or not a well-formed XML name.</exception>
        public bool TryGetRecordAttribute(string path, int index, string attrName, out string value)
        {
            return this.settings.TryGetRecordAttribute(path, index, attrName, out value);
        }

        // ------------------------------------------------------------------
        // Section queries
        // ------------------------------------------------------------------

        /// <summary>
        /// Reports whether the element at <paramref name="path"/> exists,
        /// as captured when the snapshot was taken.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <returns>true if the element exists at snapshot time, false if not.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public bool HasSection(string path)
        {
            return this.settings.HasSection(path);
        }
    }
}
