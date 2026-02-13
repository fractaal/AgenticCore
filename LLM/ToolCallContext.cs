using Godot;

public sealed class ToolCallContext {
    public Node SourceNode { get; }
    public Node SourceEntity { get; }
    public TargetResolutionWaypoint TargetWaypoint { get; }
    public Interactable TargetInteractable { get; }

    public ToolCallContext(Node sourceNode, TargetResolutionWaypoint targetWaypoint, Interactable targetInteractable) {
        SourceNode = sourceNode;
        SourceEntity = sourceNode?.GetBelongingEntity<Node>();
        TargetWaypoint = targetWaypoint;
        TargetInteractable = targetInteractable;
    }

    public T GetSourceComponent<T>() where T : Node {
        return SourceEntity?.FindNodeRecursive<T>();
    }

    public T GetTargetComponent<T>() where T : Node {
        return TargetWaypoint?.GetEntityComponent<T>();
    }
}

