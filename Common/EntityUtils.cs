using Godot;
using System;

public static class EntityUtils {
	/// <summary>
	/// Finds a node that is a descendant of the given node and is of type T
	/// </summary>
	/// <typeparam name="T">The type of node to find</typeparam>
	/// <param name="node">The node to start the search from</param>
	/// <returns>The node if found, otherwise null</returns>
	public static T FindNodeRecursive<T>(this Node node) where T : Node {
		if (node is T t) {
			return t;
		}

		foreach (var child in node.GetChildren()) {
			var result = FindNodeRecursive<T>(child);
			if (result != null) {
				return result;
			}
		}

		return null;
	}

	public static T GetBelongingEntity<T>(this Node node) where T : Node {
		return Utils.WalkUpUntilFoundEntityNode<T>(node);
	}

	public static T GetEntityComponent<T>(this Node node) where T : Node {
		var entity = node.GetBelongingEntity<Node>();

		if (!GodotObject.IsInstanceValid(entity)) {
			GD.PrintErr($"[NodeExtensions.GetEntityComponent] No entity found for node {node}");
			return null;
		}

		var component = entity.FindNodeRecursive<T>();

		if (!GodotObject.IsInstanceValid(component)) {
			GD.PrintErr($"[NodeExtensions.GetEntityComponent] No component of type {typeof(T).Name} found for entity: {entity} in node {node}");
			return null;
		}

		return component;
	}

	public static System.Collections.Generic.List<T> GetEntityComponents<T>(this Node node) where T : Node {
		var entity = node.GetBelongingEntity<Node>();
		var list = new System.Collections.Generic.List<T>();
		if (!GodotObject.IsInstanceValid(entity)) {
			GD.PrintErr($"[NodeExtensions.GetEntityComponents] No entity found for node {node}");
			return list;
		}

		void Collect(Node n) {
			foreach (var child in n.GetChildren()) {
				if (child is T t) list.Add(t);
				Collect(child);
			}
		}

		Collect(entity);
		return list;
	}
}