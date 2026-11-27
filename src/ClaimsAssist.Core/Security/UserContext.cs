using System.Text.Json;
using ClaimsAssist.Core.Knowledge;

namespace ClaimsAssist.Core.Security;

/// <summary>
/// The caller's identity as the agent sees it. In production it is built from the Microsoft Entra ID
/// token (oid and groups claims); in this prototype it comes from a development persona.
/// </summary>
public sealed record UserContext(
    string UserId,
    string DisplayName,
    string Role,
    IReadOnlySet<string> Groups,
    decimal SettlementAuthorityEur)
{
    public bool CanRead(KnowledgeChunk chunk) => chunk.AllowedGroups.Overlaps(Groups);

    public bool IsIn(string group) => Groups.Contains(group);
}

public sealed class PersonaDirectory
{
    private readonly Dictionary<string, UserContext> _personas;

    private PersonaDirectory(Dictionary<string, UserContext> personas) => _personas = personas;

    public IEnumerable<UserContext> All => _personas.Values;

    public UserContext? Find(string? id) =>
        id is not null && _personas.TryGetValue(id.Trim(), out var user) ? user : null;

    public static PersonaDirectory Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var personas = new Dictionary<string, UserContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.GetProperty("personas").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString()!;
            personas[id] = new UserContext(
                id,
                p.GetProperty("displayName").GetString()!,
                p.GetProperty("role").GetString()!,
                p.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase),
                p.GetProperty("settlementAuthorityEur").GetDecimal());
        }

        return new PersonaDirectory(personas);
    }
}
