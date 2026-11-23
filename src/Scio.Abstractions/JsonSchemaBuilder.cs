using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    /// <summary>
    /// Reflection-based JSON Schema generator for strongly-typed tool input DTOs.
    /// Replaces monolithic hand-written schema strings with type-safe C# definitions.
    /// </summary>
    public static class JsonSchemaBuilder
    {
        public static JObject BuildForType(Type dtoType)
        {
            if (dtoType == null) throw new ArgumentNullException(nameof(dtoType));
            return BuildObjectSchema(dtoType, new HashSet<Type>());
        }

        public static ToolDescriptor DescribeTool(Type handlerType, Type? inputDtoType = null)
        {
            var attr = handlerType.GetCustomAttribute<ScioToolAttribute>()
                ?? throw new InvalidOperationException($"Type '{handlerType.FullName}' is missing [ScioTool].");

            var schema = inputDtoType != null
                ? BuildForType(inputDtoType)
                : new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                    ["additionalProperties"] = false
                };

            return new ToolDescriptor
            {
                Name = attr.Name,
                Pack = attr.Pack,
                Description = attr.Description,
                ReadOnly = attr.ReadOnly,
                Destructive = attr.Destructive,
                OpenWorld = attr.OpenWorld,
                ServerSide = attr.ServerSide,
                InputSchema = schema
            };
        }

        public static string ComputeContractHash(IEnumerable<ToolDescriptor> descriptors)
        {
            var canonical = new JArray(
                descriptors
                    .OrderBy(d => d.Name, StringComparer.Ordinal)
                    .Select(d => new JObject
                    {
                        ["name"] = d.Name,
                        ["pack"] = d.Pack,
                        ["read_only"] = d.ReadOnly,
                        ["destructive"] = d.Destructive,
                        ["schema"] = d.InputSchema
                    }));

            byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToString(Formatting.None));
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(24);
                for (int i = 0; i < 12; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static JObject BuildObjectSchema(Type type, HashSet<Type> visited)
        {
            var properties = new JObject();
            var required = new JArray();

            if (!visited.Add(type))
            {
                return new JObject { ["type"] = "object" };
            }

            try
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;

                    var jsonProp = prop.GetCustomAttribute<JsonPropertyAttribute>();
                    string propName = !string.IsNullOrWhiteSpace(jsonProp?.PropertyName)
                        ? jsonProp!.PropertyName!
                        : ToSnakeCase(prop.Name);

                    var paramAttr = prop.GetCustomAttribute<ScioParamAttribute>();
                    var propSchema = BuildPropertySchema(prop.PropertyType, paramAttr, visited);

                    properties[propName] = propSchema;

                    if (paramAttr?.Required == true || jsonProp?.Required == Required.Always)
                    {
                        required.Add(propName);
                    }
                }
            }
            finally
            {
                visited.Remove(type);
            }

            var root = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };

            if (required.Count > 0)
                root["required"] = required;

            return root;
        }

        private static JObject BuildPropertySchema(Type rawType, ScioParamAttribute? attr, HashSet<Type> visited)
        {
            Type type = Nullable.GetUnderlyingType(rawType) ?? rawType;
            JObject schema;

            if (type == typeof(string))
            {
                schema = new JObject { ["type"] = "string" };
            }
            else if (type == typeof(bool))
            {
                schema = new JObject { ["type"] = "boolean" };
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            {
                schema = new JObject { ["type"] = "integer" };
            }
            else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            {
                schema = new JObject { ["type"] = "number" };
            }
            else if (type.IsEnum)
            {
                schema = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(Enum.GetNames(type).Select(ToSnakeCase))
                };
            }
            else if (type == typeof(JObject))
            {
                schema = new JObject { ["type"] = "object" };
            }
            else if (type == typeof(JArray))
            {
                schema = new JObject { ["type"] = "array" };
            }
            else if (type == typeof(JToken))
            {
                schema = new JObject();
            }
            else if (TryGetSequenceElementType(type, out Type? elementType) && elementType != null)
            {
                schema = new JObject
                {
                    ["type"] = "array",
                    ["items"] = BuildPropertySchema(elementType, null, visited)
                };
            }
            else if (typeof(IDictionary).IsAssignableFrom(type))
            {
                schema = new JObject { ["type"] = "object" };
            }
            else
            {
                schema = BuildObjectSchema(type, visited);
            }

            if (attr != null)
            {
                if (!string.IsNullOrWhiteSpace(attr.Description))
                    schema["description"] = attr.Description;

                if (attr.AllowedValues != null && attr.AllowedValues.Length > 0)
                    schema["enum"] = new JArray(attr.AllowedValues);

                if (attr.DefaultValue != null)
                    schema["default"] = JToken.FromObject(attr.DefaultValue);
            }

            return schema;
        }

        private static bool TryGetSequenceElementType(Type type, out Type? elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
            {
                if (type.IsGenericType)
                {
                    var genDef = type.GetGenericTypeDefinition();
                    if (genDef == typeof(List<>) || genDef == typeof(IList<>) ||
                        genDef == typeof(IEnumerable<>) || genDef == typeof(IReadOnlyList<>) ||
                        genDef == typeof(ICollection<>))
                    {
                        elementType = type.GetGenericArguments()[0];
                        return true;
                    }
                }
            }

            elementType = null;
            return false;
        }

        public static string ToSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && name[i - 1] != '_' && !char.IsUpper(name[i - 1]))
                        sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
