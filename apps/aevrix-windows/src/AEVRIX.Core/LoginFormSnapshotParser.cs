using System.Text.Json;

namespace Aevrix.Core;

/// <summary>
/// Strict parser for the fixed login DOM metadata schema. Unknown properties are rejected so a compromised
/// or accidentally broadened collector cannot smuggle input values, cookies, storage or raw HTML into Core.
/// </summary>
public static class LoginFormSnapshotParser
{
    private const int MaxPayloadChars = 512 * 1024;
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "totalElementCount",
        "elements"
    };

    private static readonly HashSet<string> ElementProperties = new(StringComparer.Ordinal)
    {
        "selector",
        "formKey",
        "tagName",
        "inputType",
        "name",
        "id",
        "autoComplete",
        "ariaLabel",
        "placeholder",
        "visibleText",
        "isVisible",
        "isEnabled",
        "documentOrder"
    };

    public static LoginFormSnapshot Parse(
        Uri pageUri,
        string payloadJson,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (payloadJson.Length > MaxPayloadChars)
        {
            throw new InvalidDataException("Login DOM snapshot payload exceeds the size limit.");
        }
        if (observedAtUtc == default)
        {
            throw new InvalidDataException("Login DOM snapshot timestamp is missing.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Login DOM snapshot JSON is invalid.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "Login DOM snapshot root");
            ValidateProperties(root, RootProperties, "Login DOM snapshot root");

            var schemaVersion = RequiredInt32(root, "schemaVersion");
            if (schemaVersion != LoginFormDomSnapshotScript.SchemaVersion)
            {
                throw new InvalidDataException("Unsupported login DOM snapshot schema version.");
            }

            var totalElementCount = RequiredInt32(root, "totalElementCount");
            if (totalElementCount < 0 || totalElementCount > LoginFormDomSnapshotScript.MaxElements)
            {
                throw new InvalidDataException("Login DOM snapshot was truncated or exceeded the element limit.");
            }

            var elementsNode = Required(root, "elements");
            if (elementsNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Login DOM snapshot elements must be an array.");
            }
            if (elementsNode.GetArrayLength() != totalElementCount)
            {
                throw new InvalidDataException("Login DOM snapshot element count is inconsistent.");
            }

            var elements = new LoginDomElement[totalElementCount];
            var documentOrders = new HashSet<int>();
            var index = 0;
            foreach (var node in elementsNode.EnumerateArray())
            {
                RequireObject(node, "Login DOM snapshot element");
                ValidateProperties(node, ElementProperties, "Login DOM snapshot element");

                var order = RequiredInt32(node, "documentOrder");
                if (order < 0 || !documentOrders.Add(order))
                {
                    throw new InvalidDataException("Login DOM snapshot document order is invalid or duplicated.");
                }

                elements[index++] = new LoginDomElement(
                    Selector: RequiredString(node, "selector"),
                    FormKey: RequiredString(node, "formKey"),
                    TagName: RequiredString(node, "tagName"),
                    InputType: RequiredString(node, "inputType", allowEmpty: true),
                    Name: OptionalString(node, "name"),
                    Id: OptionalString(node, "id"),
                    AutoComplete: OptionalString(node, "autoComplete"),
                    AriaLabel: OptionalString(node, "ariaLabel"),
                    Placeholder: OptionalString(node, "placeholder"),
                    VisibleText: OptionalString(node, "visibleText"),
                    IsVisible: RequiredBoolean(node, "isVisible"),
                    IsEnabled: RequiredBoolean(node, "isEnabled"),
                    DocumentOrder: order);
            }

            if (elements.Length > 0
                && (documentOrders.Min() != 0 || documentOrders.Max() != elements.Length - 1))
            {
                throw new InvalidDataException("Login DOM snapshot document order must be contiguous.");
            }

            return new LoginFormSnapshot(pageUri, elements, observedAtUtc);
        }
    }

    private static void ValidateProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException($"{description} contains an unknown or duplicate property.");
            }
        }
        if (seen.Count != allowed.Count)
        {
            throw new InvalidDataException($"{description} is missing required schema properties.");
        }
    }

    private static JsonElement Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"Login DOM snapshot property '{name}' is missing.");
        }
        return value;
    }

    private static string RequiredString(JsonElement element, string name, bool allowEmpty = false)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Login DOM snapshot property '{name}' must be a string.");
        }
        var text = value.GetString() ?? throw new InvalidDataException($"Login DOM snapshot property '{name}' is null.");
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Login DOM snapshot property '{name}' cannot be blank.");
        }
        return text;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException($"Login DOM snapshot property '{name}' must be string or null.")
        };
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new InvalidDataException($"Login DOM snapshot property '{name}' must be an integer.");
        }
        return number;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Login DOM snapshot property '{name}' must be boolean.")
        };
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be a JSON object.");
        }
    }
}