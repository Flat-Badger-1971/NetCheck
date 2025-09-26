using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.AI;

namespace NetCheck.Tools
{
    public static class ParseTools
    {
        public static List<AIFunction> GetLocalTools()
        {
            List<AIFunction> tools =
            [
                AIFunctionFactory.Create(ParseJSON, "parse_json", "Parses the content of a global.json file to return any sdk version found"),
                AIFunctionFactory.Create(ParseXML, "parse_xml", "Parses the content of a project file to return any dotnet versions found"),
            ];

            return tools;
        }

        public static List<string> ParseXML(string fileContent)
        {
            List<string> targetFrameworks = [];

            XDocument xml = XDocument.Parse(fileContent);
            targetFrameworks.AddRange(xml.Descendants("TargetFramework").Where(element => !string.IsNullOrWhiteSpace(element.Value)).Select(element => element.Value.Trim()));

            foreach (XElement element in xml.Descendants("TargetFrameworks")) // plural form
            {
                if (!string.IsNullOrWhiteSpace(element.Value))
                {
                    string[] frameworks = element.Value.Split(';');
                    targetFrameworks.AddRange(frameworks.Where(fw => !string.IsNullOrWhiteSpace(fw)).Select(fw => fw.Trim()));
                }
            }

            return targetFrameworks;
        }

        public static string ParseJSON(string globalJsonContent)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(globalJsonContent))
                {
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("sdk", out JsonElement sdkElement) && sdkElement.TryGetProperty("version", out JsonElement versionElement))
                    {
                        return versionElement.GetString();
                    }
                }
                
                return null; // SDK version not found
            }
            catch (JsonException)
            {
                throw new ArgumentException("Invalid JSON content.");
            }
        }
    }
}
