using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace NavMailGraph.Storage
{
    /// <summary>Error details parsed from an Azure or S3 &lt;Error&gt; XML body.</summary>
    internal sealed class StorageErrorInfo
    {
        internal string Code { get; set; } = "";

        internal string Message { get; set; } = "";

        internal string RequestId { get; set; } = "";
    }

    /// <summary>One listed object.</summary>
    internal sealed class ListedObject
    {
        internal ListedObject(string key, long size)
        {
            Key = key;
            Size = size;
        }

        internal string Key { get; }

        internal long Size { get; }
    }

    /// <summary>One page of a List Blobs / ListObjectsV2 response.</summary>
    internal sealed class ListPage
    {
        internal List<ListedObject> Items { get; } = new List<ListedObject>();

        /// <summary>Marker / continuation token for the next page, "" when this was the last page.</summary>
        internal string NextMarker { get; set; } = "";
    }

    /// <summary>
    /// XML parsing for storage responses with <see cref="XmlDocument"/> (no DTDs, no external resolution). Element names are
    /// matched by local name, so the S3 namespace (http://s3.amazonaws.com/doc/2006-03-01/) does not matter.
    /// </summary>
    internal static class StorageXml
    {
        /// <summary>Parses &lt;Error&gt;&lt;Code/&gt;&lt;Message/&gt;&lt;RequestId/&gt;&lt;/Error&gt;; null when the body is no such document.</summary>
        internal static StorageErrorInfo? ParseError(string? body)
        {
            XmlElement? root = Load(body);
            if (root == null || root.LocalName != "Error")
            {
                return null;
            }

            return new StorageErrorInfo
            {
                Code = ChildText(root, "Code"),
                Message = ChildText(root, "Message"),
                RequestId = ChildText(root, "RequestId"),
            };
        }

        /// <summary>Azure List Blobs: EnumerationResults/Blobs/Blob(Name, Properties/Content-Length) and NextMarker.</summary>
        internal static ListPage ParseAzureList(string body)
        {
            XmlElement root = Require(body, "EnumerationResults");
            var page = new ListPage();
            XmlElement? blobs = Child(root, "Blobs");
            if (blobs != null)
            {
                foreach (XmlNode node in blobs.ChildNodes)
                {
                    var blob = node as XmlElement;
                    if (blob == null || blob.LocalName != "Blob")
                    {
                        continue;
                    }

                    XmlElement? nameElement = Child(blob, "Name");
                    string name = nameElement == null ? "" : nameElement.InnerText;
                    if (nameElement != null && string.Equals(nameElement.GetAttribute("Encoded"), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        name = Uri.UnescapeDataString(name);
                    }

                    XmlElement? properties = Child(blob, "Properties");
                    long size = properties == null ? -1 : ParseLong(ChildText(properties, "Content-Length"));
                    page.Items.Add(new ListedObject(name, size));
                }
            }

            page.NextMarker = ChildText(root, "NextMarker");
            return page;
        }

        /// <summary>S3 ListObjectsV2: ListBucketResult/Contents(Key, Size), IsTruncated, NextContinuationToken.</summary>
        internal static ListPage ParseS3List(string body)
        {
            XmlElement root = Require(body, "ListBucketResult");
            var page = new ListPage();
            foreach (XmlNode node in root.ChildNodes)
            {
                var contents = node as XmlElement;
                if (contents == null || contents.LocalName != "Contents")
                {
                    continue;
                }

                // Keys are taken verbatim: leading/trailing spaces are part of the key (StorageKeys.Validate allows them).
                page.Items.Add(new ListedObject(ChildRawText(contents, "Key"), ParseLong(ChildText(contents, "Size"))));
            }

            bool truncated = string.Equals(ChildText(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
            page.NextMarker = truncated ? ChildRawText(root, "NextContinuationToken") : "";
            if (truncated && page.NextMarker.Length == 0)
            {
                throw new FormatException("ListObjectsV2 response is truncated but has no NextContinuationToken");
            }

            return page;
        }

        /// <summary>InitiateMultipartUploadResult/UploadId.</summary>
        internal static string ParseUploadId(string body)
        {
            string id = ChildText(Require(body, "InitiateMultipartUploadResult"), "UploadId");
            if (id.Length == 0)
            {
                throw new FormatException("InitiateMultipartUploadResult has no UploadId");
            }

            return id;
        }

        /// <summary>CompleteMultipartUploadResult/ETag ("" when absent).</summary>
        internal static string ParseCompleteETag(string body)
        {
            XmlElement? root = Load(body);
            return root == null ? "" : ChildText(root, "ETag");
        }

        /// <summary>Root element of <paramref name="body"/> or null when it is empty or not well-formed XML.</summary>
        internal static XmlElement? Load(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                };
                // PreserveWhitespace: otherwise a whitespace-only text node (e.g. the key "  ") would be dropped.
                var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                using (var reader = XmlReader.Create(new StringReader(body!.TrimStart('﻿')), settings))
                {
                    doc.Load(reader);
                }

                return doc.DocumentElement;
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static XmlElement Require(string body, string rootName)
        {
            XmlElement? root = Load(body);
            if (root == null || root.LocalName != rootName)
            {
                throw new FormatException("unexpected response: expected an XML <" + rootName + "> document");
            }

            return root;
        }

        private static XmlElement? Child(XmlElement parent, string localName)
        {
            foreach (XmlNode node in parent.ChildNodes)
            {
                var element = node as XmlElement;
                if (element != null && element.LocalName == localName)
                {
                    return element;
                }
            }

            return null;
        }

        /// <summary>Trimmed text of a child element (codes, messages, numbers, flags).</summary>
        private static string ChildText(XmlElement parent, string localName)
        {
            return ChildRawText(parent, localName).Trim();
        }

        /// <summary>Untrimmed text of a child element (object keys, continuation tokens).</summary>
        private static string ChildRawText(XmlElement parent, string localName)
        {
            XmlElement? child = Child(parent, localName);
            return child == null ? "" : child.InnerText;
        }

        private static long ParseLong(string text)
        {
            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : -1;
        }
    }
}
