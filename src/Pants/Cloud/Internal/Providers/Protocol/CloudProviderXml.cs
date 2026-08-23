using System.Xml;
using System.Xml.Linq;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

static class CloudProviderXml
{
    public static XDocument ParseList(
        string body,
        string expectedRoot,
        string provider)
    {
        try
        {
            using var textReader = new StringReader(body);
            using var xmlReader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            if (document.Root?.Name.LocalName != expectedRoot)
            {
                throw new PantsIOException(
                    $"{provider} LIST response root must be {expectedRoot}.");
            }

            return document;
        }
        catch (XmlException exception)
        {
            throw new PantsIOException($"{provider} LIST response XML is malformed.", exception);
        }
    }

    public static string? TryReadElementValue(string body, string elementName)
    {
        try
        {
            using var textReader = new StringReader(body);
            using var xmlReader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            return document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == elementName)?
                .Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
