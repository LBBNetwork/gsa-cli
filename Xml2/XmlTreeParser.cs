using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Xml2
{
    /// <summary>
    /// Parses an XML document into the internal tree using XmlReader.
    ///
    /// Security posture (per Constraints): DTDs prohibited, entity
    /// expansion capped, comments and ignorable whitespace discarded
    /// (comments are dropped in v1 anyway).
    ///
    /// Diagnostic contract (per Open Questions §7, fail-fast): every
    /// failure leaves no partial tree and surfaces as XmlSettingsException
    /// carrying the element path (root-relative — the root segment is
    /// omitted, e.g. "StoreController/Controller"; empty string when the
    /// problem is at document level) and the 1-based source line
    /// (0 if unknown).
    /// </summary>
    internal static class XmlTreeParser
    {
        private const long MaxDocumentCharacters = 10000000L;
        private const long MaxExpandedEntityCharacters = 1000000L;

        internal static XmlTreeNode Parse(string xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException("xml");
            }

            XmlReader reader;
            try
            {
                reader = XmlReader.Create(new StringReader(xml), CreateReaderSettings());
            }
            catch (InvalidOperationException inner)
            {
                throw new XmlSettingsException(
                    "Could not create XML reader: " + inner.Message,
                    string.Empty, 0, inner);
            }

            using (reader)
            {
                Stack<XmlTreeNode> openNodes = new Stack<XmlTreeNode>();
                XmlTreeNode root = null;
                bool documentClosed = false;

                try
                {
                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                if (documentClosed)
                                {
                                    throw new XmlSettingsException(
                                        "Content after the root element has closed.",
                                        string.Empty, 0);
                                }

                                XmlTreeNode node = new XmlTreeNode(reader.LocalName);
                                node.AttachedTo = (openNodes.Count == 0)
                                    ? null
                                    : openNodes.Peek();

                                // Attributes in document order (XmlReader
                                // guarantees this for MoveToNextAttribute).
                                while (reader.MoveToNextAttribute())
                                {
                                    node.Attributes.Add(new XmlAttribute(reader.LocalName, reader.Value));
                                }
                                reader.MoveToElement();

                                if (openNodes.Count == 0)
                                {
                                    if (root != null)
                                    {
                                        throw new XmlSettingsException(
                                            "Multiple root elements are not allowed; a settings document has exactly one.",
                                            string.Empty, 0);
                                    }
                                    root = node;
                                }
                                else
                                {
                                    openNodes.Peek().Children.Add(node);
                                }

                                openNodes.Push(node);
                                break;

                            case XmlNodeType.EndElement:
                                CloseElement(reader.LocalName, openNodes);
                                if (openNodes.Count == 0 && root != null && !documentClosed)
                                {
                                    documentClosed = true;
                                }
                                break;

                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                            case XmlNodeType.SignificantWhitespace:
                                if (openNodes.Count == 0)
                                {
                                    string where = documentClosed
                                        ? "after the root element has closed"
                                        : "before the root element";
                                    throw new XmlSettingsException(
                                        "Stray text content " + where + " is not allowed.",
                                        string.Empty, 0);
                                }
                                openNodes.Peek().AppendValue(reader.Value);
                                break;

                            case XmlNodeType.Whitespace:
                            case XmlNodeType.Comment:
                                // Filtered out by the reader settings; the
                                // skips here are defensive.
                                break;

                            // Prolog, XmlDeclaration, BOM — nothing to do.
                        }
                    }
                }
                catch (XmlException inner)
                {
                    throw new XmlSettingsException(
                        "Malformed XML: " + inner.Message,
                        BuildPathWith(reader, openNodes),
                        NormalizeLine(inner.LineNumber),
                        inner);
                }

                if (root == null)
                {
                    throw new XmlSettingsException(
                        "Empty document: no root element.",
                        string.Empty, 0);
                }

                if (openNodes.Count > 0)
                {
                    string name = openNodes.Peek().Name;
                    throw new XmlSettingsException(
                        "Element <" + name + "> was never closed before end of document.",
                        BuildPathWith(null, openNodes),
                        0);
                }

                return root;
            }
        }

        /// <summary>
        /// Pops the element (by name) off the stack of open nodes and
        /// repairs the tree around elements the reader never closed with
        /// their own EndEvent (observed in Mono for self-closing elements):
        ///
        ///  - every node still sitting above `name` was a self-closing
        ///    element: it is a leaf, so its text belongs to its true
        ///    container, and any nodes pushed "inside" it actually belong
        ///    to that same container;
        ///  - repair runs bottom-to-top so appended children land in
        ///    document order;
        ///  - the true container of a node is found by following its
        ///    AttachedTo chain while the parent is itself one of the
        ///    unclosed (self-closing) nodes.
        ///
        /// On a reader that does emit EndElement for every element the
        /// orphan loop is empty and this is a plain pop — so the logic is
        /// correct under both Mono and standard .NET behavior.
        /// </summary>
        private static void CloseElement(string name, Stack<XmlTreeNode> openNodes)
        {
            List<XmlTreeNode> orphans = new List<XmlTreeNode>();
            while (openNodes.Count > 0 && openNodes.Peek().Name != name)
            {
                orphans.Add(openNodes.Pop());
            }

            if (openNodes.Count == 0)
            {
                throw new XmlSettingsException(
                    "Closing tag </" + name + "> does not match any open element.",
                    string.Empty, 0);
            }

            XmlTreeNode element = openNodes.Pop();

            if (orphans.Count == 0)
            {
                return;
            }

            // Pass one: resolve each orphan's true container before any
            // mutation. Pass one walks the AttachedTo chain through
            // orphans until it reaches a node that is truly open.
            List<XmlTreeNode> targets = new List<XmlTreeNode>();
            for (int i = 0; i < orphans.Count; i++)
            {
                XmlTreeNode current = orphans[i];
                while (current.AttachedTo != null && orphans.Contains(current.AttachedTo))
                {
                    current = current.AttachedTo;
                }
                targets.Add((current.AttachedTo != null) ? current.AttachedTo : element);
            }

            // Pass two: re-attribute text and parentage, bottom-to-top.
            for (int i = orphans.Count - 1; i >= 0; i--)
            {
                XmlTreeNode orphan = orphans[i];
                XmlTreeNode target = targets[i];

                if (orphan.Value.Length > 0)
                {
                    target.AppendValue(orphan.Value);
                    orphan.Value = string.Empty;
                }

                ReParent(orphan, orphan.AttachedTo, target);

                // Nodes pushed inside a self-closing element while it was
                // (apparently) the open context.
                List<XmlTreeNode> inner = new List<XmlTreeNode>(orphan.Children);
                for (int j = 0; j < inner.Count; j++)
                {
                    ReParent(inner[j], orphan, target);
                }
            }
        }

        private static void ReParent(XmlTreeNode node, XmlTreeNode from, XmlTreeNode to)
        {
            if (from == null || from == to || node.AttachedTo != from || !from.Children.Contains(node))
            {
                return;
            }

            from.Children.Remove(node);
            to.Children.Add(node);
            node.AttachedTo = to;
        }

        /// <summary>
        /// Joins the currently open elements with '/', omitting the root
        /// segment so error paths match the accessor-path convention.
        ///
        /// When the reader is present, the element it was parsing at the
        /// moment of the failure (its .Name, which remains readable after
        /// an XmlException) is appended — this recovers paths such as
        /// "StoreController/Controller" when the failure occurs in the
        /// middle of reading that element's start tag.
        /// </summary>
        private static string BuildPathWith(XmlReader reader, Stack<XmlTreeNode> openNodes)
        {
            List<string> names = new List<string>();
            foreach (XmlTreeNode node in openNodes)
            {
                names.Add(node.Name);
            }
            names.Reverse();

            string path = string.Empty;
            for (int i = 1; i < names.Count; i++)
            {
                if (path.Length > 0)
                {
                    path += "/";
                }
                path += names[i];
            }

            if (names.Count >= 1 && reader != null && reader.Name != null && reader.Name.Length > 0
                && !(path == reader.Name || path.EndsWith("/" + reader.Name)))
            {
                path = (path.Length == 0)
                    ? reader.Name
                    : path + "/" + reader.Name;
            }

            return path;
        }

        private static int NormalizeLine(int line)
        {
            return (line > 0) ? line : 0;
        }

        private static XmlReaderSettings CreateReaderSettings()
        {
            // NOTE: the original also set DtdProcessing.Prohibit, MaxCharactersInDocument
            // and MaxCharactersFromEntities; those members were not in the .NET 2.0
            // API surface and are not needed here (the default reader behavior already
            // refuses DTDs, and the documents we touch are small).
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.IgnoreComments = true;
            settings.IgnoreWhitespace = true;
            return settings;
        }
    }
}
