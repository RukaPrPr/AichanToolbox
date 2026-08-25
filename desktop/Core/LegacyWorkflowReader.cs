using System.Globalization;
using System.Xml.Linq;

namespace AichanToolbox.Core;

internal static class LegacyWorkflowReader
{
    public static WorkflowDocument Read(string path)
    {
        var document = XDocument.Load(path);
        var root = document.Root ?? throw new InvalidOperationException("工作流文件为空。");
        var result = new WorkflowDocument
        {
            Version = 4,
            Parallelism = ReadInt(root.Element("Parallelism"), 6)
        };

        var nodes = root.Element("Nodes")?.Elements("NodeWorkflowNode") ?? Enumerable.Empty<XElement>();
        foreach (var source in nodes)
        {
            result.Nodes.Add(new WorkflowNode
            {
                Id = ReadString(source.Element("Id"), Guid.NewGuid().ToString("N")),
                Type = ReadString(source.Element("Type"), "ConvertJpg"),
                Title = ReadString(source.Element("Title"), "节点"),
                X = ReadInt(source.Element("X"), 0),
                Y = ReadInt(source.Element("Y"), 0),
                Width = ReadInt(source.Element("Width"), 0),
                Height = ReadInt(source.Element("Height"), 0),
                Data = new NodeSettings
                {
                    SizeOperator = ReadString(source.Element("SizeOperator"), ">="),
                    SizeMb = ReadDouble(source.Element("SizeMb"), 1),
                    ScalePercent = ReadInt(source.Element("ScalePercent"), 80),
                    QualityPercent = ReadInt(source.Element("QualityPercent"), 100),
                    WidthEnabled = ReadBool(source.Element("WidthEnabled"), true),
                    HeightEnabled = ReadBool(source.Element("HeightEnabled"), true),
                    WidthOperator = ReadString(source.Element("WidthOperator"), ">="),
                    HeightOperator = ReadString(source.Element("HeightOperator"), ">="),
                    WidthValue = ReadInt(source.Element("WidthValue"), 1920),
                    HeightValue = ReadInt(source.Element("HeightValue"), 1080),
                    ResolutionJoin = ReadString(source.Element("ResolutionJoin"), "AND"),
                    SameFolder = ReadBool(source.Element("SameFolder"), true),
                    OutputDirectory = ReadString(source.Element("OutputDirectory"), ""),
                    ReplaceOriginal = ReadBool(source.Element("ReplaceOriginal"), false)
                }
            });
        }

        var connections = root.Element("Connections")?.Elements("NodeWorkflowConnection") ?? Enumerable.Empty<XElement>();
        foreach (var source in connections)
        {
            result.Connections.Add(new WorkflowConnection
            {
                Id = Guid.NewGuid().ToString("N"),
                FromNodeId = ReadString(source.Element("FromNodeId"), ""),
                FromPort = MigratePort(result, ReadString(source.Element("FromNodeId"), ""), ReadString(source.Element("FromPort"), "out")),
                ToNodeId = ReadString(source.Element("ToNodeId"), ""),
                ToPort = ReadString(source.Element("ToPort"), "in")
            });
        }
        return result;
    }

    private static string MigratePort(WorkflowDocument document, string nodeId, string port)
    {
        var node = document.Nodes.FirstOrDefault(value => value.Id == nodeId);
        if (node?.Type != "FormatFilter") return port;
        return port switch { "match" => "png", "else" => "other", _ => port };
    }

    private static string ReadString(XElement? value, string fallback)
        => string.IsNullOrWhiteSpace(value?.Value) ? fallback : value!.Value;
    private static int ReadInt(XElement? value, int fallback)
        => int.TryParse(value?.Value, out var result) ? result : fallback;
    private static bool ReadBool(XElement? value, bool fallback)
        => bool.TryParse(value?.Value, out var result) ? result : fallback;
    private static double ReadDouble(XElement? value, double fallback)
        => double.TryParse(value?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : fallback;
}
