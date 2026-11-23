using System;

namespace Scio.Abstractions
{
    /// <summary>
    /// Declares an MCP tool exposed by a SCIO Revit MCP v4 domain module.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ScioToolAttribute : Attribute
    {
        public string Name { get; }
        public string Pack { get; }
        public string Description { get; }
        public bool ReadOnly { get; set; }
        public bool Destructive { get; set; }
        public bool OpenWorld { get; set; }
        public bool ServerSide { get; set; }

        public ScioToolAttribute(string name, string pack, string description)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Pack = pack ?? throw new ArgumentNullException(nameof(pack));
            Description = description ?? string.Empty;
        }
    }

    /// <summary>
    /// Annotates a parameter property on a tool's strongly-typed input DTO
    /// so <see cref="JsonSchemaBuilder"/> can generate its MCP JSON Schema automatically.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ScioParamAttribute : Attribute
    {
        public string? Name { get; }
        public string Description { get; }
        public bool Required { get; set; }
        public string[]? AllowedValues { get; set; }
        public string[]? EnumValues
        {
            get => AllowedValues;
            set => AllowedValues = value;
        }
        public object? DefaultValue { get; set; }

        public ScioParamAttribute(string description)
        {
            Description = description ?? string.Empty;
        }

        public ScioParamAttribute(string name, string description)
        {
            Name = name;
            Description = description ?? string.Empty;
        }
    }
}
