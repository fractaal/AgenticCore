using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Interface for objects that can be interacted with via tool calls (LLM and Player UI).
///
/// New model: Tools can be declared via attributes on methods. By default, this base
/// class will reflect for [Tool]-annotated methods to build the schema and execute calls.
/// Subclasses may override to customize or to maintain legacy behavior during migration.
/// </summary>
public abstract partial class Interactable : Node {
	/// <summary>
	/// Get available tools/actions for this object for a specific caller/target context.
	/// Default implementation uses attribute-driven reflection.
	/// </summary>
	/// <param name="ctx">Caller-aware listing context (never null when called by engine paths)</param>
	/// <returns>List of tools that can be called on this object</returns>
	public virtual List<Tool> GetAvailableTools(ToolCallContext ctx) {
		return ToolSchemaBuilder.BuildSchemas(this, ctx);
	}


	/// <summary>
	/// Execute a tool call on this object with full caller/target context.
	/// Default implementation invokes a [Tool]-annotated method via reflection binder.
	/// </summary>
	/// <param name="toolCall">The tool call to execute</param>
	/// <param name="ctx">Tool call context containing the source (caller) and resolved target</param>
	/// <returns>Structured tool call result for the LLM (ContentParts-only outward)</returns>
	public virtual Task<ToolCallResult> ExecuteToolCallAsync(ToolCall toolCall, ToolCallContext ctx) {
		return ToolInvocation.InvokeAsync(this, toolCall, ctx);
	}
}
