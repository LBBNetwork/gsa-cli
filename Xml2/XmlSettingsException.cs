using System;

namespace Xml2
{
    /// <summary>
    /// Thrown when a settings document cannot be loaded or saved, or when a
    /// path cannot be resolved. Carries the offending element path and the
    /// source line (1-based, 0 if unknown) so callers get clean diagnostics
    /// instead of a raw parser exception.
    /// </summary>
    public class XmlSettingsException : Exception
    {
        private string elementPath;
        private int line;

        public XmlSettingsException()
            : base()
        {
            this.elementPath = string.Empty;
            this.line = 0;
        }

        public XmlSettingsException(string message)
            : base(message)
        {
            this.elementPath = string.Empty;
            this.line = 0;
        }

        /// <summary>
        /// Creates an exception with a full path/line diagnostic.
        /// </summary>
        /// <param name="message">Human-readable description of the problem.</param>
        /// <param name="elementPath">Path to the offending element; empty string if top level or unknown.</param>
        /// <param name="line">1-based source line; 0 if unknown.</param>
        public XmlSettingsException(string message, string elementPath, int line)
            : base(message)
        {
            this.elementPath = (elementPath == null) ? string.Empty : elementPath;
            this.line = line;
        }

        /// <summary>
        /// Creates an exception that wraps an underlying .NET failure
        /// (e.g. file I/O) with a full path/line diagnostic.
        /// </summary>
        /// <param name="message">Human-readable description of the problem.</param>
        /// <param name="elementPath">Path to the offending element; empty string if top level or unknown.</param>
        /// <param name="line">1-based source line; 0 if unknown.</param>
        /// <param name="innerException">The underlying failure being wrapped.</param>
        public XmlSettingsException(string message, string elementPath, int line, Exception innerException)
            : base(message, innerException)
        {
            this.elementPath = (elementPath == null) ? string.Empty : elementPath;
            this.line = line;
        }

        /// <summary>
        /// Path to the offending element (root-level: empty string;
        /// otherwise slash-separated segments, e.g. "StoreController/Controller").
        /// </summary>
        public string ElementPath
        {
            get { return this.elementPath; }
        }

        /// <summary>
        /// 1-based source line of the offending construct; 0 if unknown.
        /// </summary>
        public int Line
        {
            get { return this.line; }
        }

        /// <summary>
        /// Formats the diagnostic as "message (path, line N)".
        /// </summary>
        public string FormatDiagnostic()
        {
            if (this.elementPath.Length == 0 && this.line <= 0)
            {
                return this.Message;
            }
            if (this.elementPath.Length == 0 && this.line > 0)
            {
                return this.Message + " (line " + this.line + ")";
            }
            if (this.elementPath.Length > 0 && this.line <= 0)
            {
                return this.Message + " (" + this.elementPath + ")";
            }
            return this.Message + " (" + this.elementPath + ", line " + this.line + ")";
        }
    }
}
