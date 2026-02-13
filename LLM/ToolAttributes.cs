using System;

public enum ToolVisibility {
    Public,
    Private
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute {
    public string Name { get; }
    public string Description { get; }
    public ToolVisibility Visibility { get; set; } = ToolVisibility.Public;

    public ToolAttribute(string name, string description) {
        Name = name;
        Description = description;
    }
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ToolArgAttribute : Attribute {
    public string Name { get; }
    public string Description { get; }
    public bool Required { get; }

    public ToolArgAttribute(string name, string description, bool required = true) {
        Name = name;
        Description = description;
        Required = required;
    }
}


[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ToolWhenAttribute : Attribute {
    public string GuardMemberName { get; }
    public ToolWhenAttribute(string guardMemberName) { GuardMemberName = guardMemberName; }
}


[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresProximityAttribute : Attribute {
    public float RadiusMeters { get; }
    public RequiresProximityAttribute(float radiusMeters) {
        RadiusMeters = radiusMeters < 0 ? 0 : radiusMeters;
    }
}



// Opt-out marker: by default public tools require proximity. Apply this to allow remote execution.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NoProximityRequiredAttribute : Attribute { }


// Public tools may opt-in to disallow self-targets; enforced at listing and call-time
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DisallowSelfTargetAttribute : Attribute { }
