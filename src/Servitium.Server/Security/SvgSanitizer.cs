using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using Servitium.Domain.Common;

namespace Servitium.Server.Security;

internal static class SvgSanitizer
{
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "defs", "linearGradient",
        "radialGradient", "stop", "clipPath", "mask", "title", "desc", "use", "symbol",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "viewBox", "width", "height", "x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "points", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-opacity", "opacity", "transform", "offset", "stop-color", "stop-opacity", "clip-path", "mask", "preserveAspectRatio",
        "role", "aria-label", "focusable",
    };

    internal static async Task<byte[]> SanitizeAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(source, settings);
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            XNamespace svgNamespace = "http://www.w3.org/2000/svg";
            if (document.Root is null || document.Root.Name != svgNamespace + "svg")
            {
                throw new DomainException("validation-failed", "The uploaded SVG root is invalid.");
            }

            foreach (XElement element in document.Root.DescendantsAndSelf())
            {
                if (element.Name.Namespace != svgNamespace || !AllowedElements.Contains(element.Name.LocalName))
                {
                    throw new DomainException("validation-failed", "The uploaded SVG contains a disallowed element.");
                }

                foreach (XAttribute attribute in element.Attributes().ToArray())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    string name = attribute.Name.LocalName;
                    string value = attribute.Value.Trim();
                    bool localReference = name is "href" && value.StartsWith('#') && value.Length <= 129;
                    if ((!AllowedAttributes.Contains(name) && !localReference) ||
                        name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("data:", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("url(", StringComparison.OrdinalIgnoreCase) ||
                        (name is "href" && !localReference))
                    {
                        throw new DomainException("validation-failed", "The uploaded SVG contains a disallowed attribute or external reference.");
                    }
                }
            }

            using MemoryStream output = new();
            XmlWriterSettings writerSettings = new()
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = true,
                Async = true,
            };
            using (var writer = XmlWriter.Create(output, writerSettings))
            {
                await document.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            return output.ToArray();
        }
        catch (XmlException exception)
        {
            throw new DomainException("validation-failed", exception.Message);
        }
    }
}
