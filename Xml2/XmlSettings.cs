using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Xml2
{
    /// <summary>
    /// A settings document: one root element and its ordered tree of
    /// elements, attributes and text values, addressed by slash-separated
    /// paths (e.g. "StoreDetails/StoreNumber").
    ///
    /// Construction is through the static <see cref="Load"/> and
    /// <see cref="Parse"/> factories; mutation and read methods follow in
    /// the scalar, attribute and record tiers (see the project plan).
    ///
    /// Concurrency: every public instance method takes the same internal
    /// monitor, and <see cref="Snapshot"/> hands back a deep copy, so any
    /// mix of concurrent readers, writers and snapshot takers is safe.
    ///
    /// Error channels (documented on each method):
    ///   - bad document content  -> XmlSettingsException (path + line)
    ///   - bad call arguments   -> ArgumentException family
    ///   - file-level problems  -> IOException family (e.g. Load of a
    ///     missing file, Save to an unwritable location)
    ///
    /// Determinism: a given tree always renders to identical bytes —
    /// UTF-8 (no BOM), CRLF line endings, two-space indentation, original
    /// attribute and child order. See XmlTreeWriter for the canonical form.
    /// </summary>
    public class XmlSettings
    {
        private readonly object monitor = new object();

        /// <summary>
        /// The document root as an in-memory tree. Never null on an
        /// instance created by Load/Parse; null is reserved for the
        /// planned empty-document constructor.
        /// </summary>
        private XmlTreeNode root;

        /// <summary>
        /// Creates a settings instance around an already-parsed root
        /// element. The caller owns the tree from that point on.
        /// </summary>
        internal XmlSettings(XmlTreeNode root)
        {
            if (root == null)
            {
                throw new ArgumentNullException("root");
            }

            this.root = root;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Loads a settings document from a file and parses it.
        /// </summary>
        /// <param name="path">
        /// File system path of the XML file. Read as UTF-8 (a BOM, if
        /// present, is honored and not re-emitted).
        /// </param>
        /// <returns>A ready-to-use settings instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="XmlSettingsException">The file's content is malformed XML or violates the loading rules (for example: multiple roots, a DTD, or an internal entity).</exception>
        /// <exception cref="System.IO.IOException">The file cannot be read (missing file, access denied, ...).</exception>
        public static XmlSettings Load(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }

            // File.ReadAllText auto-detects a BOM and otherwise assumes
            // UTF-8 — exactly the encoding contract this library writes
            // with, and no external dependency.
            string xml = File.ReadAllText(path);
            return Parse(xml);
        }

        /// <summary>
        /// Parses settings content from a string.
        /// </summary>
        /// <param name="xml">
        /// Complete XML document text, with a single root element.
        /// </param>
        /// <returns>A ready-to-use settings instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
        /// <exception cref="XmlSettingsException">The content is malformed or violates the loading rules; <see cref="XmlSettingsException.ElementPath"/> and <see cref="XmlSettingsException.Line"/> identify the first problem.</exception>
        public static XmlSettings Parse(string xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException("xml");
            }

            XmlTreeNode root = XmlTreeParser.Parse(xml);
            return new XmlSettings(root);
        }

        /// <summary>
        /// Renders the current document to canonical XML text (see
        /// class remarks for the byte-level contract: UTF-8, CRLF,
        /// two-space indent, original attribute and child order).
        /// </summary>
        /// <returns>
        /// The full document, including the XML declaration. The returned
        /// string is safe to persist verbatim as UTF-8 without a BOM.
        /// </returns>
        /// <exception cref="XmlSettingsException">The document is empty (no root), or the tree contains names (element or attribute) that are not well-formed XML names; the offending element path is reported.</exception>
        public string ToXml()
        {
            lock (this.monitor)
            {
                return this.RenderLocked();
            }
        }

        /// <summary>
        /// Saves the current document to a file in canonical form — the
        /// exact bytes <see cref="ToXml"/> would return: UTF-8 without a
        /// BOM, CRLF line endings, two-space indentation.
        /// </summary>
        /// <param name="path">File system path to create or overwrite.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="XmlSettingsException">The document is empty (no root), or the tree contains names that are not well-formed XML names; the offending element path is reported.</exception>
        /// <exception cref="System.IO.IOException">The file cannot be written or the directory does not exist.</exception>
        /// <remarks>
        /// The write is a single truncate-and-write of the file; the
        /// library does not use temporary-file atomicity (fine for small
        /// settings files; do not rely on the old bytes surviving a crash
        /// mid-write).
        /// </remarks>
        public void Save(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }

            string xml;
            lock (this.monitor)
            {
                xml = this.RenderLocked();
            }

            // New instance of the encoding so callers can never see a
            // shared/encrypted UTF-8 encoder state.
            File.WriteAllText(path, xml, new UTF8Encoding(false));
        }

        /// <summary>
        /// Takes a point-in-time deep copy of the current document for
        /// lock-free reading: other threads may keep mutating this
        /// instance while the snapshot is in use, and the snapshot keeps
        /// returning exactly the state that existed at the moment the
        /// copy was taken.
        /// </summary>
        /// <returns>
        /// A read-only <see cref="XmlSettingsSnapshot"/> of the current
        /// tree; later writes to this instance are not visible through it.
        /// </returns>
        /// <remarks>
        /// The copy is a full deep copy - only the tree itself is copied,
        /// no file I/O is involved. Reading a snapshot never takes this
        /// instance's monitor, so snapshot reads never block on, or are
        /// blocked by, concurrent writers (see the class remarks for the
        /// concurrency contract).
        /// </remarks>
        /// <exception cref="InvalidOperationException">The document is empty (no root element to copy).</exception>
        public XmlSettingsSnapshot Snapshot()
        {
            return new XmlSettingsSnapshot(this.CloneRoot());
        }

        // ------------------------------------------------------------------
        // Scalar tier - element text values
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the text content of the element at <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative, e.g. "StoreDetails/StoreNumber". See <see cref="SettingPaths"/> rules.</param>
        /// <returns>
        /// The element's own text content. Distinguishing the cases:
        /// <list type="bullet">
        /// <item><description><b>null</b> - the element does not exist;</description></item>
        /// <item><description><b>"" (empty string)</b> - the element exists but holds no text, including elements whose content is entirely child elements;</description></item>
        /// <item><description>otherwise the accumulated text content.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public string GetValue(string path)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveLocked(path);

                if (node == null)
                {
                    return null;
                }

                return node.Value;
            }
        }

        /// <summary>
        /// Reads the text content of the element at <paramref name="path"/>
        /// and reports whether the element exists, unlike
        /// <see cref="GetValue"/>, where "absent" and "present but empty"
        /// are indistinguishable.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="value"/>
        /// On return: the element's text content (which may be "").
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the element exists (and
        /// <paramref name="value"/> carries its text); <see langword="false"/> if it does not (and <paramref name="value"/> is null).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public bool TryGetValue(string path, out string value)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveLocked(path);

                if (node == null)
                {
                    value = null;
                    return false;
                }

                value = node.Value;
                return true;
            }
        }

        /// <summary>
        /// Sets the text content of the element at <paramref name="path"/>,
        /// creating it (and any missing leading elements) if needed.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="value">New text content; null is stored as empty string.</param>
        /// <remarks>
        /// Two behaviors worth knowing before relying on this:
        /// <list type="bullet">
        /// <item>
        /// <description>The target is made into a <b>text-value leaf</b>: any attributes and child elements it previously carried are discarded, because a single element cannot at once be a settings leaf and a container. (Keep attributes on the element via the attribute tier, which does not touch text.)</description>
        /// </item>
        /// <item>
        /// <description>If the document is still empty (no root at all), the <b>first segment of the path becomes the root element name</b>: SetValue("GSAToolkit/StoreDetails/StoreNumber", "1234") on a fresh document creates root "GSAToolkit" and the rest of the chain below it.</description>
        /// </item>
        /// </list>
        /// Creation order is document order: the new element is appended as
        /// the last child of its parent, so Save/ToXml output stays fully
        /// deterministic.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public void SetValue(string path, string value)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveOrCreateLocked(path);
                node.Children.Clear();
                node.Attributes.Clear();
                node.Value = (value == null) ? string.Empty : value;
            }
        }

        /// <summary>
        /// Removes the element at <paramref name="path"/> and everything
        /// under it. Removing a non-existent path is a harmless no-op, so
        /// callers can drop settings without first checking for them.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <remarks>
        /// The document root itself cannot be removed (it has no parent);
        /// a single-segment path only matches a child of the root.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public void RemoveValue(string path)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                this.RemoveChildLocked(path);
            }
        }

        // ------------------------------------------------------------------
        // Attribute tier - element attributes
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the value of the attribute attrName on the element at
        /// path.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name (must be a well-formed XML name; case-sensitive).</param>
        /// <returns>
        /// The attribute's value - which may be empty string if it was
        /// stored empty; or null if either the element or the attribute
        /// does not exist.
        /// </returns>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public string GetAttribute(string path, string attrName)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveLocked(path);

                if (node == null)
                {
                    return null;
                }

                return node.GetAttributeValue(attrName);
            }
        }

        /// <summary>
        /// Reads the value of the attribute attrName on the element at
        /// path and reports whether it exists, unlike GetAttribute, where
        /// "absent" and "present but empty" are indistinguishable.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name (case-sensitive).</param>
        /// <param name="value">On return: the attribute's value, which may be empty string.</param>
        /// <returns>
        /// True if the element exists and carries the attribute; false
        /// otherwise (and value is null).
        /// </returns>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public bool TryGetAttribute(string path, string attrName, out string value)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveLocked(path);

                if (node == null)
                {
                    value = null;
                    return false;
                }

                int index = node.FindAttribute(attrName);

                if (index < 0)
                {
                    value = null;
                    return false;
                }

                value = node.Attributes[index].Value;
                return true;
            }
        }

        /// <summary>
        /// Sets the attribute attrName on the element at path, creating
        /// the element (and any missing leading elements) if needed.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name (must be a well-formed XML name).</param>
        /// <param name="value">New attribute value; null is stored as empty string.</param>
        /// <remarks>
        /// Behavior notes:
        ///   - Unlike SetValue, setting an attribute does NOT discard the
        ///     element's text or child elements - attributes coexist with
        ///     content in XML, and an attribute on a container element is
        ///     perfectly normal (e.g. "Departments/id").
        ///   - If the attribute already exists, its value is replaced IN
        ///     PLACE, preserving the attribute's position among the
        ///     element's attributes; otherwise it is appended last. This
        ///     keeps serialized output fully deterministic while behaving
        ///     like a settings map (one value per attribute name).
        ///   - Like SetValue, an empty document is seeded with its root
        ///     from the first path segment.
        ///   - A name that is not a well-formed XML name is rejected here,
        ///     at the moment of the call, instead of surfacing later at
        ///     ToXml/Save time.
        /// </remarks>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public void SetAttribute(string path, string attrName, string value)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveOrCreateLocked(path);
                string storedValue = (value == null) ? string.Empty : value;

                int index = node.FindAttribute(attrName);

                if (index >= 0)
                {
                    // Values on the internal attribute type are readonly,
                    // so the swap-in is a new instance - but the attribute
                    // keeps its position, which is what deterministic
                    // output depends on.
                    node.Attributes[index] = new XmlAttribute(attrName, storedValue);
                }
                else
                {
                    node.Attributes.Add(new XmlAttribute(attrName, storedValue));
                }
            }
        }

        /// <summary>
        /// Removes the attribute attrName from the element at path.
        /// Removing something that is not there is a harmless no-op.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <param name="attrName">Attribute name to remove.</param>
        /// <remarks>
        /// Only the attribute is removed; the element (and its text, other
        /// attributes and children) stays in place.
        /// </remarks>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public void RemoveAttribute(string path, string attrName)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode node = this.ResolveLocked(path);

                if (node == null)
                {
                    return;
                }

                int index = node.FindAttribute(attrName);

                if (index >= 0)
                {
                    node.Attributes.RemoveAt(index);
                }
            }
        }

        // ------------------------------------------------------------------
        // Record tier - repeated child elements (reader half)
        //
        // A "record" is one occurrence of a repeated element under a
        // container: Departments[0], Departments[1], ...  The record-path
        // reading shared by every record tier method:
        //   "Departments"           -> container = document root
        //   "StoreDetails/Options"  -> container = first StoreDetails
        // i.e. the LAST path segment is the name of the repeated element
        // and the leading segments locate the container (resolved as the
        // first matching chain, like the other tiers). Occurrence index
        // is document order, 0-based; it is stable under document
        // mutation only to the extent the order itself is - which is
        // exactly what the mutating half of this tier guarantees.
        // ------------------------------------------------------------------

        /// <summary>
        /// Counts the occurrences of the record element named by path in
        /// its container. Zero (not an exception) when the container is
        /// missing or holds no such children.
        /// </summary>
        /// <param name="path">Record path: "Departments" or "StoreDetails/Options" - the last segment names the repeated element, the leading segments locate the container.</param>
        /// <returns>The number of matching child elements (0 when absent).</returns>
        /// <exception cref="ArgumentNullException">path is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad or empty segment.</exception>
        public int Count(string path)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    return 0;
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];

                return parent.FindAllChildren(recordName).Count;
            }
        }

        /// <summary>
        /// Finds the index of the first record (in document order) whose
        /// attribute attrName equals searchValue exactly (case-sensitive).
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="attrName">Name of the attribute to match (must be a well-formed XML name).</param>
        /// <param name="searchValue">Value to look for; null is treated as empty string. Records that carry the attribute with an empty value are found by "" (or by null).</param>
        /// <returns>
        /// The 0-based index of the first matching record - usable directly
        /// as the index argument of the other record tier methods; or -1
        /// when the container is absent, holds no matching records, or no
        /// record carries a matching attribute.
        /// </returns>
        /// <remarks>Matching is equality, not substring: FindByAttribute(
        /// "RecentPaths", "type", "c") finds exactly type="c", never
        /// type="custom" or similar.</remarks>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public int FindByAttribute(string path, string attrName, string searchValue)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    return -1;
                }

                string target = (searchValue == null) ? string.Empty : searchValue;
                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].GetAttributeValue(attrName) == target)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        /// <summary>
        /// Reads the text content of the record at an index and reports
        /// whether that index is in range, unlike plain reads where
        /// "absent" and "empty" collide.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index, as returned by <see cref="FindByAttribute"/>.</param>
        /// <param name="value">On return: the record's own text content, which may be empty string.</param>
        /// <returns>
        /// True if the record exists at that index (and value carries its
        /// text); false if the container is missing, the record name holds
        /// no children there, or the index is out of range (including
        /// negative indices - all "not found", never exceptions).
        /// </returns>
        /// <remarks>This reads the record's TEXT content. For records that
        /// carry their data in attributes, use the attribute tier or
        /// record attribute accessors with the same index convention.
        /// </remarks>
        /// <exception cref="ArgumentNullException">path is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad or empty segment.</exception>
        public bool TryGetRecordValue(string path, int index, out string value)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    value = null;
                    return false;
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                if (index < 0 || index >= records.Count)
                {
                    value = null;
                    return false;
                }

                value = records[index].Value;
                return true;
            }
        }

        /// <summary>
        /// Reads the attribute attrName of the record at an index and
        /// reports whether that record exists and carries the attribute,
        /// unlike plain reads where "record absent", "attribute absent" and
        /// "attribute present but empty" collide.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index, as returned by <see cref="FindByAttribute"/>.</param>
        /// <param name="attrName">Attribute name to read (must be a well-formed XML name; case-sensitive).</param>
        /// <param name="value">On return: the attribute's value, which may be empty string.</param>
        /// <returns>
        /// True if the record exists at that index AND carries the
        /// attribute (and value carries it, possibly ""); false - with
        /// value set to null - if the container is missing, the index is
        /// out of range (including negative), or the record does not carry
        /// the attribute.
        /// </returns>
        /// <remarks>This is the indexed companion of
        /// <see cref="TryGetAttribute"/>, which always reads the FIRST
        /// occurrence; use this one when the record is addressed by a
        /// <see cref="FindByAttribute"/> result.</remarks>
        /// <exception cref="ArgumentNullException">path or attrName is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad segment, or attrName is empty or not a well-formed XML name.</exception>
        public bool TryGetRecordAttribute(string path, int index, string attrName, out string value)
        {
            SettingPaths.Validate(path, "path");
            SettingPaths.ValidateName(attrName, "attrName");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    value = null;
                    return false;
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                if (index < 0 || index >= records.Count)
                {
                    value = null;
                    return false;
                }

                XmlTreeNode record = records[index];
                int attrIndex = record.FindAttribute(attrName);

                if (attrIndex < 0)
                {
                    value = null;
                    return false;
                }

                value = record.Attributes[attrIndex].Value;
                return true;
            }
        }

        // ------------------------------------------------------------------
        // Record tier - repeated child elements (mutating half)
        //
        // Shared rules (Set/Update/Add/RemoveRecord):
        //   - fields travel as parallel arrays (names, values): names.Length
        //     must equal values.Length, checked BEFORE the tree is touched;
        //   - bad field names fail fast: null element -> ArgumentNullException,
        //     empty or malformed -> ArgumentException;
        //   - null field values are stored as the empty string;
        //   - after Set/Add the record is an attribute-only leaf (its
        //     previous text and children are cleared); Update preserves them;
        //   - attribute order in the output is exactly the order passed;
        //   - Set/Update REQUIRE the index to exist (ArgumentException);
        //     Remove treats an out-of-range index as a no-op (idempotent),
        //     mirroring the Try* readers.
        // ------------------------------------------------------------------

        /// <summary>
        /// Replaces the record at index: its previous attributes, text and
        /// children are discarded, and it is rebuilt with exactly the fields
        /// given, in the order given, as an attribute-only leaf.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index of the record to replace; must exist.</param>
        /// <param name="names">Attribute names, in the order they should appear.</param>
        /// <param name="values">Attribute values; null values are stored as empty string.</param>
        /// <exception cref="ArgumentNullException">path, names, values or a names element is null.</exception>
        /// <exception cref="ArgumentException">path is empty or malformed; names/values differ in length; a name is empty or not a well-formed XML name; or index is out of range.</exception>
        public void SetRecord(string path, int index, string[] names, string[] values)
        {
            SettingPaths.Validate(path, "path");

            if (names == null)
            {
                throw new ArgumentNullException("names");
            }

            if (values == null)
            {
                throw new ArgumentNullException("values");
            }

            if (names.Length != values.Length)
            {
                throw new ArgumentException(
                    "names.Length (" + names.Length + ") must equal values.Length (" + values.Length + ")",
                    "names");
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == null)
                {
                    throw new ArgumentNullException("names[" + i + "]");
                }

                if (names[i].Length == 0 || !XmlNames.IsWellFormedName(names[i]))
                {
                    throw new ArgumentException(
                        "Field name '" + names[i] + "' (index " + i + ") is not a well-formed XML name.",
                        "names");
                }
            }

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    throw new ArgumentException(
                        "Record '" + path + "' does not exist (no container or no matching children).",
                        "index");
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                if (index < 0 || index >= records.Count)
                {
                    throw new ArgumentException(
                        "No record at index " + index + " (" + records.Count + " present) for '" + path + "'",
                        "index");
                }

                XmlTreeNode record = records[index];

                // Full replacement: the record becomes an attribute-only leaf.
                record.Children.Clear();
                record.Value = string.Empty;
                record.Attributes.Clear();

                for (int i = 0; i < names.Length; i++)
                {
                    record.Attributes.Add(
                        new XmlAttribute(names[i], (values[i] == null) ? string.Empty : values[i]));
                }
            }
        }

        /// <summary>
        /// Updates the record at index, touching only the fields named:
        /// each named attribute is overwritten in place (position kept) or
        /// appended, in the order given; attributes NOT named and the
        /// record's text are left exactly as they were.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index of the record to update; must exist.</param>
        /// <param name="names">Attribute names to set (order only matters for attributes being added).</param>
        /// <param name="values">Attribute values; null values are stored as empty string.</param>
        /// <exception cref="ArgumentNullException">path, names, values or a names element is null.</exception>
        /// <exception cref="ArgumentException">path is empty or malformed; names/values differ in length; a name is empty or not a well-formed XML name; or index is out of range.</exception>
        public void UpdateRecord(string path, int index, string[] names, string[] values)
        {
            SettingPaths.Validate(path, "path");

            if (names == null)
            {
                throw new ArgumentNullException("names");
            }

            if (values == null)
            {
                throw new ArgumentNullException("values");
            }

            if (names.Length != values.Length)
            {
                throw new ArgumentException(
                    "names.Length (" + names.Length + ") must equal values.Length (" + values.Length + ")",
                    "names");
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == null)
                {
                    throw new ArgumentNullException("names[" + i + "]");
                }

                if (names[i].Length == 0 || !XmlNames.IsWellFormedName(names[i]))
                {
                    throw new ArgumentException(
                        "Field name '" + names[i] + "' (index " + i + ") is not a well-formed XML name.",
                        "names");
                }
            }

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    throw new ArgumentException(
                        "Record '" + path + "' does not exist (no container or no matching children).",
                        "index");
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                if (index < 0 || index >= records.Count)
                {
                    throw new ArgumentException(
                        "No record at index " + index + " (" + records.Count + " present) for '" + path + "'",
                        "index");
                }

                XmlTreeNode record = records[index];

                for (int i = 0; i < names.Length; i++)
                {
                    int attrIndex = record.FindAttribute(names[i]);
                    string storedValue = (values[i] == null) ? string.Empty : values[i];

                    if (attrIndex >= 0)
                    {
                        // Replace in place so existing attributes keep their
                        // positions - only the order of NEW ones matters.
                        record.Attributes[attrIndex] = new XmlAttribute(names[i], storedValue);
                    }
                    else
                    {
                        record.Attributes.Add(new XmlAttribute(names[i], storedValue));
                    }
                }
            }
        }

        /// <summary>
        /// Sets the element text of the record at index, leaving every
        /// attribute on the record exactly as it is. Records whose value
        /// is carried as element text, e.g.
        /// <c>&lt;Default id="QTY"&gt;0&lt;/Default&gt;</c>, need this in
        /// addition to the attribute tier: AddRecord, SetRecord and
        /// UpdateRecord write attributes only.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index of the record to set; must exist.</param>
        /// <param name="value">New element text; null clears the text (stored as empty string).</param>
        /// <exception cref="ArgumentNullException">path is null.</exception>
        /// <exception cref="ArgumentException">path is empty or malformed; or index is out of range (use AddRecord when the record should not exist yet).</exception>
        public void SetRecordValue(string path, int index, string value)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    throw new ArgumentException(
                        "Record '" + path + "' does not exist (no container or no matching children).",
                        "index");
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];
                List<XmlTreeNode> records = parent.FindAllChildren(recordName);

                if (index < 0 || index >= records.Count)
                {
                    throw new ArgumentException(
                        "No record at index " + index + " (" + records.Count + " present) for '" + path + "'",
                        "index");
                }

                XmlTreeNode record = records[index];
                record.Value = (value == null) ? string.Empty : value;
            }
        }

        /// <summary>
        /// Removes the record at index from its container; subsequent
        /// records shift down by one index. An out-of-range index (or a
        /// missing container) is a no-op - deletion is idempotent.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="index">0-based occurrence index of the record to remove.</param>
        /// <exception cref="ArgumentNullException">path is null.</exception>
        /// <exception cref="ArgumentException">path is empty or contains a bad or empty segment.</exception>
        public void RemoveRecord(string path, int index)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                XmlTreeNode parent;

                if (!this.ResolveRecordSetLocked(path, out parent))
                {
                    return;
                }

                string[] recordSegments = SettingPaths.Split(path);
                string recordName = recordSegments[recordSegments.Length - 1];

                // Match occurrences in document order; remove the nth one.
                int occurrence = -1;

                for (int i = 0; i < parent.Children.Count; i++)
                {
                    XmlTreeNode child = parent.Children[i];

                    if (child.Name == recordName)
                    {
                        occurrence++;

                        if (occurrence == index)
                        {
                            parent.Children.RemoveAt(i);
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Appends a new record - an attribute-only leaf carrying exactly
        /// the fields given, in the order given - as the LAST occurrence in
        /// its container, creating the container and any missing ancestors
        /// (root included, seeded from the first segment like SetValue) if
        /// they do not exist yet.
        /// </summary>
        /// <param name="path">Record path, read as in <see cref="Count"/>.</param>
        /// <param name="names">Attribute names, in the order they should appear.</param>
        /// <param name="values">Attribute values; null values are stored as empty string.</param>
        /// <returns>
        /// The index of the new record within its container - the number of
        /// occurrences that already existed. Usable directly as the index
        /// argument of UpdateRecord, SetRecord and RemoveRecord.
        /// </returns>
        /// <exception cref="ArgumentNullException">path, names, values or a names element is null.</exception>
        /// <exception cref="ArgumentException">path is empty or malformed; names/values differ in length; a name is empty or not a well-formed XML name; or the document is empty and path is a single segment (there would be nothing to seed the root from).</exception>
        public int AddRecord(string path, string[] names, string[] values)
        {
            SettingPaths.Validate(path, "path");

            if (names == null)
            {
                throw new ArgumentNullException("names");
            }

            if (values == null)
            {
                throw new ArgumentNullException("values");
            }

            if (names.Length != values.Length)
            {
                throw new ArgumentException(
                    "names.Length (" + names.Length + ") must equal values.Length (" + values.Length + ")",
                    "names");
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == null)
                {
                    throw new ArgumentNullException("names[" + i + "]");
                }

                if (names[i].Length == 0 || !XmlNames.IsWellFormedName(names[i]))
                {
                    throw new ArgumentException(
                        "Field name '" + names[i] + "' (index " + i + ") is not a well-formed XML name.",
                        "names");
                }
            }

            lock (this.monitor)
            {
                string[] segments = SettingPaths.Split(path);
                string recordName = segments[segments.Length - 1];
                XmlTreeNode container;
                int newIndex;

                if (this.ResolveRecordSetLocked(path, out container))
                {
                    // Container exists: append as the last occurrence; the
                    // new index is the occurrences count before the add.
                    int occurrences = container.FindAllChildren(recordName).Count;

                    XmlTreeNode record = new XmlTreeNode(recordName);
                    record.AttachedTo = container;

                    for (int i = 0; i < names.Length; i++)
                    {
                        record.Attributes.Add(
                            new XmlAttribute(names[i], (values[i] == null) ? string.Empty : values[i]));
                    }

                    container.Children.Add(record);
                    newIndex = occurrences;
                }
                else if (segments.Length < 2)
                {
                    // Empty document + single-segment path would need the
                    // record to BE the root - not expressible in this tier.
                    throw new ArgumentException(
                        "Cannot add record '" + path + "' to an empty document: use a two-segment path such as 'RootName/" + path + "'.",
                        "path");
                }
                else
                {
                    // Container chain (root included) missing: create it.
                    // ResolveOrCreateLocked builds the WHOLE path, seeded
                    // from the first segment exactly like SetValue; the new
                    // trailing node is a fresh record, hence index 0.
                    XmlTreeNode record = this.ResolveOrCreateLocked(path);

                    for (int i = 0; i < names.Length; i++)
                    {
                        record.Attributes.Add(
                            new XmlAttribute(names[i], (values[i] == null) ? string.Empty : values[i]));
                    }

                    newIndex = 0;
                }

                return newIndex;
            }
        }

        // ------------------------------------------------------------------
        // Sections tier - structural existence and removal
        // ------------------------------------------------------------------

        /// <summary>
        /// Reports whether the element named by <paramref name="path"/> exists in
        /// the document tree.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <returns>
        /// true when the full path resolves to an element (of any kind -
        /// scalar, record container, or plain section), or false when the
        /// document is empty or any segment is missing.
        /// </returns>
        /// <remarks>
        /// This is the structural existence check: it returns the same
        /// value for a scalar element as <see cref="TryGetValue"/> would,
        /// but also answers "does this section exist" for elements that
        /// carry only children or attributes - cases where GetValue /
        /// TryGetValue cannot distinguish "absent" from "not a scalar".
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public bool HasSection(string path)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                return this.ResolveLocked(path) != null;
            }
        }

        /// <summary>
        /// Removes the element at <paramref name="path"/> together with
        /// its entire subtree - child elements, attributes and text.
        /// Removing a non-existent path is a harmless no-op, so callers
        /// can drop sections without first checking with
        /// <see cref="HasSection"/>.
        /// </summary>
        /// <param name="path">Slash-separated path, root-relative.</param>
        /// <remarks>
        /// Paths are root-relative: a single-segment path names a child
        /// of the root, so the document root itself can never be removed
        /// (it has no parent). Removing the matched element removes
        /// everything under it as well; the scalar tier's
        /// <see cref="RemoveValue"/> carries the same contract and both
        /// are backed by one shared implementation.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains a bad or empty segment.</exception>
        public void RemoveSection(string path)
        {
            SettingPaths.Validate(path, "path");

            lock (this.monitor)
            {
                this.RemoveChildLocked(path);
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        /// <summary>
        /// Deep-copies the current root tree for the caller. The copy is
        /// independent: mutations of the live document (or of the copy) do
        /// not affect the other.
        /// </summary>
        internal XmlTreeNode CloneRoot()
        {
            lock (this.monitor)
            {
                if (this.root == null)
                {
                    return null;
                }

                return this.root.Clone();
            }
        }

        private string RenderLocked()
        {
            if (this.root == null)
            {
                throw new XmlSettingsException(
                    "Settings document is empty: there is no root element to write.",
                    string.Empty, 0);
            }

            // The writer throws XmlSettingsException (with the element
            // path and line) for any name it cannot serialize.
            return XmlTreeWriter.ToText(this.root);
        }

        /// <summary>
        /// Resolves a validated path to the element it names, or null if
        /// any segment is missing. Caller must hold the monitor.
        /// </summary>
        private XmlTreeNode ResolveLocked(string path)
        {
            string[] segments = SettingPaths.Split(path);
            XmlTreeNode current = this.root;

            for (int i = 0; current != null && i < segments.Length; i++)
            {
                current = current.FindChild(segments[i]);
            }

            return current;
        }

        /// <summary>
        /// Resolves a validated path, creating the element (and missing
        /// leading elements, in document order) as needed. If the document
        /// has no root yet, the first segment becomes the root name. Caller
        /// must hold the monitor.
        /// </summary>
        private XmlTreeNode ResolveOrCreateLocked(string path)
        {
            string[] segments = SettingPaths.Split(path);
            int firstSegment = 0;
            XmlTreeNode current = this.root;

            if (current == null)
            {
                current = new XmlTreeNode(segments[0]);
                this.root = current;
                firstSegment = 1;
            }

            for (int i = firstSegment; i < segments.Length; i++)
            {
                XmlTreeNode child = current.FindChild(segments[i]);

                if (child == null)
                {
                    child = new XmlTreeNode(segments[i]);

                    // Keep the same parent-bookkeeping invariant the parser
                    // maintains, so the tree stays uniform no matter how it
                    // was built.
                    child.AttachedTo = current;
                    current.Children.Add(child);
                }

                current = child;
            }

            return current;
        }

        /// <summary>
        /// Removes the element named by a validated path (and its whole
        /// subtree) from its parent. No-op when any segment is missing.
        /// The root can never be named by a path, so it can never be
        /// removed. Shared by <see cref="RemoveValue"/> and
        /// <see cref="RemoveSection"/> - the tiers document the contract
        /// for their own callers, this method only performs the surgery.
        /// Caller must hold the monitor.
        /// </summary>
        private void RemoveChildLocked(string path)
        {
            string[] segments = SettingPaths.Split(path);

            if (this.root == null)
            {
                return;
            }

            // Walk down to the parent of the target, giving up (no-op)
            // the moment anything along the way is missing.
            XmlTreeNode current = this.root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = current.FindChild(segments[i]);

                if (current == null)
                {
                    return;
                }
            }

            XmlTreeNode target = current.FindChild(segments[segments.Length - 1]);

            if (target != null)
            {
                current.Children.Remove(target);
            }
        }

        /// <summary>
        /// Resolves a record path to its container element: the last path
        /// segment is the record's (repeated element's) name and NOT part
        /// of the container lookup; the leading segments locate the
        /// container. Returns false (parent = null) when the document has
        /// no root or any leading segment is missing. Caller must hold the
        /// monitor.
        /// </summary>
        private bool ResolveRecordSetLocked(string path, out XmlTreeNode parent)
        {
            string[] segments = SettingPaths.Split(path);
            XmlTreeNode current = this.root;

            // Walk only the leading segments; the last one names the
            // records, not the container.
            for (int i = 0; current != null && i < segments.Length - 1; i++)
            {
                current = current.FindChild(segments[i]);
            }

            parent = current;
            return current != null;
        }
    }
}
