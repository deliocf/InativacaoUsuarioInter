using Microsoft.Extensions.Options;

namespace InativacaoUsuarioInter.Service.Configuracao;

/// <summary>Valida todas as seções de configuração na partida e reúne todas as falhas (FR-004, SC-006).</summary>
internal static class ValidacaoPartida
{
    public static IReadOnlyList<string> Validar(IServiceProvider servicos)
    {
        ArgumentNullException.ThrowIfNull(servicos);
        var falhas = new List<string>();
        Coletar<SettingsOptions>(servicos, falhas);
        Coletar<SharePointFolderOptions>(servicos, falhas);
        Coletar<ConnectionStringOptions>(servicos, falhas);
        Coletar<ProcessamentoOptions>(servicos, falhas);
        Coletar<ResilienciaOptions>(servicos, falhas);
        return falhas.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void Coletar<T>(IServiceProvider servicos, List<string> falhas)
        where T : class
    {
        try
        {
            _ = servicos.GetRequiredService<IOptions<T>>().Value;
        }
        catch (OptionsValidationException excecao)
        {
            falhas.AddRange(excecao.Failures);
        }
        catch (InvalidOperationException excecao)
        {
            // Falha de conversão no binding (ex.: texto em chave numérica); a mensagem cita a chave.
            falhas.Add(excecao.Message);
        }
    }
}
