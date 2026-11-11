using System;
using System.IO;
using System.Text;
using System.Xml;

namespace Xml2
{
    /// <summary>
    /// Serializes the internal tree to a canonical XML text form using
    /// XmlWriter.
    ///
    /// Determinism (per Requirements, Non-goals and Open Questions §5):
    ///   - document, attribute and child order come from the tree, which
    ///     keeps the order the document arrived in (stable across runs);
    ///   - two-space indent, CRLF line endings, UTF-8, UTF-8 declared;
    ///   - the same tree therefore always produces byte-identical output.
    ///
    /// Fail-fast (per Open Questions §7): a tree that cannot be written
    /// (e.g. an invalid element or attribute name) raises
    /// XmlSettingsException naming the element, never a raw writer
    /// exception.
    /// </summary>
    internal static class XmlTreeWriter
    {
        private const string Indent = "  ";

        internal static string ToText(XmlTreeNode root)
        {
            if (root == null)
            {
                throw new ArgumentNullException("root");
            }

            StringBuilder path = new StringBuilder();
            // Serialize into a memory stream whose encoding is part of the
            // XmlWriter (a StringWriter would force "utf-16" into the
            // declaration); re-decode with the same UTF-8.
            MemoryStream ms = new MemoryStream();
            XmlWriter writer;
            try
            {
                writer = XmlWriter.Create(ms, CreateWriterSettings());
            }
            catch (InvalidOperationException inner)
            {
                throw new XmlSettingsException(
                    "Could not create XML writer: " + inner.Message,
                    string.Empty, 0, inner);
            }

            try
            {
                using (writer)
                {
                    writer.WriteStartDocument();
                    WriteNode(writer, root, path);
                    writer.WriteEndDocument();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (ArgumentException inner)
            {
                throw new XmlSettingsException(
                    "Cannot write element <" + path + ">: " + inner.Message,
                    path.ToString(), 0, inner);
            }
            catch (InvalidOperationException inner)
            {
                throw new XmlSettingsException(
                    "Cannot write element <" + path + ">: " + inner.Message,
                    path.ToString(), 0, inner);
            }
        }

        private static XmlWriterSettings CreateWriterSettings()
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.IndentChars = Indent;
            settings.NewLineChars = "\r\n";
            settings.Encoding = new UTF8Encoding(false);
            return settings;
        }

        private static void WriteNode(XmlWriter writer, XmlTreeNode node, StringBuilder path)
        {
            int startLength = path.Length;
            if (path.Length > 0)
            {
                path.Append('/');
            }
            path.Append(node.Name);

            // Validate names up front: the buffered XmlWriter may defer its
            // own name validation until flush, at which point the element
            // path can no longer be trusted. Failing here gives a precise
            // diagnostic for the offending element (or attribute).
            if (!XmlNames.IsWellFormedName(node.Name))
            {
                throw new XmlSettingsException(
                    "Cannot write element <" + path + ">: '" + node.Name +
                    "' is not a well-formed XML element name.",
                    path.ToString(), 0);
            }

            for (int i = 0; i < node.Attributes.Count; i++)
            {
                XmlAttribute check = node.Attributes[i];
                if (!XmlNames.IsWellFormedName(check.Name))
                {
                    throw new XmlSettingsException(
                        "Cannot write element <" + path + ">: '" + check.Name +
                        "' is not a well-formed XML attribute name.",
                        path.ToString(), 0);
                }
            }

            try
            {
                writer.WriteStartElement(node.Name);

                // Attributes in tree (document) order.
                for (int i = 0; i < node.Attributes.Count; i++)
                {
                    XmlAttribute attribute = node.Attributes[i];
                    writer.WriteAttributeString(attribute.Name, attribute.Value);
                }

                if (node.Children.Count > 0)
                {
                    // Children in tree (document) order. Any text content
                    // of a container element is written before its
                    // children — a fixed canonical placement, so mixed
                    // content still serializes deterministically.
                    if (node.Value.Length > 0)
                    {
                        writer.WriteString(node.Value);
                    }

                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        WriteNode(writer, node.Children[i], path);
                    }
                }
                else if (node.Value.Length > 0)
                {
                    // Plain text leaf.
                    writer.WriteString(node.Value);
                }
                // A leaf with no text and no children serializes as a
                // self-closing element (e.g. <Recent/>), which keeps the
                // output stable and unambiguous.

                writer.WriteEndElement();
            }
            finally
            {
                // Rewind the path (the '/' plus the name appended above).
                path.Length = startLength;
            }
        }

    }
}
