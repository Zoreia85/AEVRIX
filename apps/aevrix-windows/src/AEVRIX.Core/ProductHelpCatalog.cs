namespace Aevrix.Core;

public sealed record ProductHelpItem(string Id,string Title,string Summary,string? LearnMoreKey=null);

public static class ProductHelpCatalog
{
    private static readonly IReadOnlyDictionary<string,ProductHelpItem> Items =
        new Dictionary<string,ProductHelpItem>(StringComparer.Ordinal)
        {
            ["project.new"]=new("project.new","Novo projeto","Cria um workspace isolado para uma nova análise.","projects"),
            ["analysis.start"]=new("analysis.start","Iniciar análise","Orquestra os especialistas autorizados e registra provas de execução.","analysis"),
            ["evidence.open"]=new("evidence.open","Evidências","Mostra observações, validações e sua proveniência sem misturar níveis de confiança.","evidence"),
            ["blueprint.open"]=new("blueprint.open","Blueprint","Abre o mapa reconstruído somente a partir de conhecimento admitido e proveniência verificável.","blueprint"),
            ["engine.status"]=new("engine.status","Motor local","Mostra a saúde do EngineHost isolado usado nas tarefas locais.","engine"),
            ["security.status"]=new("security.status","Segurança","Explica isolamento, conexão segura e limites ativos antes da execução.","security")
        };

    public static ProductHelpItem Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Items.TryGetValue(id,out var item)
            ? item
            : throw new KeyNotFoundException("Unknown AEVRIX help item.");
    }

    public static IReadOnlyCollection<ProductHelpItem> All => Items.Values.ToArray();
}
