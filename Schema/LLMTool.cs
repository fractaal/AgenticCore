using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

public static class LLMTool {
	public static Tool Function(string name, string description, Parameters parameters, List<string> required) {
		if (parameters != null && required != null) {
			parameters.Required = required;
		}
		return new Tool {
			Type = "function",
			Function = new Function {
				Name = name,
				Description = description,
				Parameters = parameters
			}
		};
	}

	public static Parameters Parameters(params Property[] properties) {
		var dict = new Dictionary<string, Property>();
		foreach (var prop in properties) {
			dict[prop.Name] = prop;
		}
		return new Parameters {
			Type = "object",
			Properties = dict,
			Required = new List<string>()
		};
	}

	public static Property Property(string name, string type, string description) {
		return new Property {
			Name = name,
			Type = type,
			Description = description
		};
	}

	public static string ToJson(Tool tool) {
		var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };
		return JsonSerializer.Serialize(tool, options);
	}

	public static string AllToolsJson(List<Tool> tools) {
		var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };
		return JsonSerializer.Serialize(tools, options);
	}
}
