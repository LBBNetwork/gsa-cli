using System.Collections.Generic;

namespace Xml2
{
    /// <summary>
    /// One attribute of an element, kept in document order (deterministic
    /// output depends on this).
    /// </summary>
    internal sealed class XmlAttribute
    {
        public readonly string Name;
        public readonly string Value;

        public XmlAttribute(string name, string value)
        {
            this.Name = name;
            this.Value = value;
        }
    }

    /// <summary>
    /// One element of the settings tree. Text content is stored in Value
    /// (empty string when the element has no text); attributes and children
    /// keep document order.
    /// </summary>
    internal sealed class XmlTreeNode
    {
        public readonly string Name;
        public string Value;
        public readonly List<XmlAttribute> Attributes;
        public readonly List<XmlTreeNode> Children;

        /// <summary>
        /// The element whose open context this node was pushed into. Used
        /// by close-time repair to restore true parentage when a node was
        /// pushed "inside" a self-closing element. Updated whenever the
        /// node is re-parented.
        /// </summary>
        public XmlTreeNode AttachedTo;

        public XmlTreeNode(string name)
        {
            this.Name = name;
            this.Value = string.Empty;
            this.Attributes = new List<XmlAttribute>();
            this.Children = new List<XmlTreeNode>();
        }

        /// <summary>
        /// Appends to this element's accumulated text content.
        /// </summary>
        public void AppendValue(string text)
        {
            this.Value = this.Value + text;
        }

        /// <summary>
        /// Returns the value of the named attribute, or null if the element
        /// does not carry it. Exact, case-sensitive match.
        /// </summary>
        public string GetAttributeValue(string name)
        {
            int index = FindAttribute(name);
            if (index < 0)
            {
                return null;
            }
            return this.Attributes[index].Value;
        }

        /// <summary>
        /// Returns the index of the named attribute, or -1 if not present.
        /// Exact, case-sensitive match.
        /// </summary>
        public int FindAttribute(string name)
        {
            List<XmlAttribute> attributes = this.Attributes;
            for (int i = 0; i < attributes.Count; i++)
            {
                if (attributes[i].Name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns the first child element with the given name, or null if
        /// none. Exact, case-sensitive match.
        /// </summary>
        public XmlTreeNode FindChild(string name)
        {
            List<XmlTreeNode> children = this.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Name == name)
                {
                    return children[i];
                }
            }
            return null;
        }

        /// <summary>
        /// The children in document order with the given name — a new list
        /// each call, so callers may add to or remove from it freely.
        /// </summary>
        public List<XmlTreeNode> FindAllChildren(string name)
        {
            List<XmlTreeNode> result = new List<XmlTreeNode>();
            for (int i = 0; i < this.Children.Count; i++)
            {
                if (this.Children[i].Name == name)
                {
                    result.Add(this.Children[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// A full deep copy of this node and its subtree.
        /// </summary>
        public XmlTreeNode Clone()
        {
            XmlTreeNode copy = new XmlTreeNode(this.Name);
            copy.Value = this.Value;
            for (int i = 0; i < this.Attributes.Count; i++)
            {
                XmlAttribute source = this.Attributes[i];
                copy.Attributes.Add(new XmlAttribute(source.Name, source.Value));
            }
            for (int i = 0; i < this.Children.Count; i++)
            {
                copy.Children.Add(this.Children[i].Clone());
            }
            return copy;
        }
    }
}
