using InativacaoUsuarioInter.Service.Entrada.Modelos;
using Microsoft.Extensions.Options;

namespace InativacaoUsuarioInter.Service.Configuracao;

// Validadores executados na partida (ValidateOnStart, FR-004/FR-005, contracts/configuracao.md).
// As mensagens citam a chave e a regra; nunca exibem valores de ConnectionString:*.

/// <summary><c>Settings:Instancia</c> e <c>Settings:IntervalSeconds</c>.</summary>
internal sealed class ValidadorSettings : IValidateOptions<SettingsOptions>
{
    public ValidateOptionsResult Validate(string? name, SettingsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var falhas = new List<string>();
        if (ConfiguracaoServico.InterpretarInstancia(options.Instancia) is null)
        {
            falhas.Add($"Settings:Instancia deve ser 'Inter' ou 'Condominio' (valor informado: '{options.Instancia ?? "<ausente>"}').");
        }

        if (ConfiguracaoServico.InterpretarIntervalo(options.IntervalSeconds) is null)
        {
            falhas.Add(
                $"Settings:IntervalSeconds deve ser um número inteiro entre {LimitesConfiguracao.IntervaloMinimo} e " +
                $"{LimitesConfiguracao.IntervaloMaximo} segundos, faixa 10–21600 (valor informado: '{options.IntervalSeconds ?? "<ausente>"}').");
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

/// <summary>Pastas obrigatórias por instância: Inter exige as duas; Condomínio exige a sua. Sempre absolutas.</summary>
internal sealed class ValidadorPastas(IConfiguration configuracao) : IValidateOptions<SharePointFolderOptions>
{
    public ValidateOptionsResult Validate(string? name, SharePointFolderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var instancia = ConfiguracaoServico.InterpretarInstancia(configuracao["Settings:Instancia"]);
        var falhas = new List<string>();
        if (instancia == Instancia.Inter)
        {
            ExigirAbsoluto(options.FolderPathInter, "SharePointFolder:FolderPathInter", "Inter", falhas);
        }

        if (instancia is not null)
        {
            ExigirAbsoluto(options.FolderPathCondominio, "SharePointFolder:FolderPathCondominio", instancia.Value.ToString(), falhas);
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }

    private static void ExigirAbsoluto(string? caminho, string chave, string instancia, List<string> falhas)
    {
        if (string.IsNullOrWhiteSpace(caminho))
        {
            falhas.Add($"{chave} é obrigatório para a instância {instancia}.");
        }
        else if (!Path.IsPathFullyQualified(caminho))
        {
            falhas.Add($"{chave} deve ser um caminho absoluto.");
        }
    }
}

/// <summary>
/// <c>ConnectionString:WAccess</c> obrigatória nas duas instâncias (Clarification Q1). Detecta a chave antiga
/// <c>WAccessInter</c> sem lê-la (research R-16).
/// </summary>
internal sealed class ValidadorConexao(IConfiguration configuracao) : IValidateOptions<ConnectionStringOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.WAccess))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            DetectorChaveLegada.Presente(configuracao)
                ? DetectorChaveLegada.Mensagem
                : "ConnectionString:WAccess é obrigatória (conexão com o WAccess da própria instalação).");
    }
}

/// <summary>Detecta a chave legada <c>ConnectionString:WAccessInter</c> (research R-16). O valor nunca é lido.</summary>
internal static class DetectorChaveLegada
{
    public const string ChaveLegada = "ConnectionString:WAccessInter";
    public const string Mensagem = "Renomeie ConnectionString:WAccessInter para ConnectionString:WAccess.";

    public static bool Presente(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        return configuracao.GetSection(ChaveLegada).Exists();
    }
}

/// <summary>Faixas de <c>Processamento:*</c>.</summary>
internal sealed class ValidadorProcessamento : IValidateOptions<ProcessamentoOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessamentoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var falhas = new List<string>();
        Faixa(options.EstabilizacaoSegundos, 1, 300, "Processamento:EstabilizacaoSegundos", falhas);
        Faixa(options.MaximoRegistros, 1, 1000, "Processamento:MaximoRegistros", falhas);
        Faixa(options.TamanhoMaximoBytes, 1, 1_048_576, "Processamento:TamanhoMaximoBytes", falhas);
        Faixa(options.AlertaPendenciaHoras, 1, 720, "Processamento:AlertaPendenciaHoras", falhas);
        Faixa(options.TimeoutDiarioMs, 1000, 60000, "Processamento:TimeoutDiarioMs", falhas);
        if (!string.IsNullOrWhiteSpace(options.DiretorioEstado) && !Path.IsPathFullyQualified(options.DiretorioEstado))
        {
            falhas.Add("Processamento:DiretorioEstado deve ser um caminho absoluto local.");
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }

    internal static void Faixa(double valor, double minimo, double maximo, string chave, List<string> falhas)
    {
        if (valor < minimo || valor > maximo)
        {
            falhas.Add(FormattableString.Invariant($"{chave} deve estar entre {minimo} e {maximo} (valor informado: {valor})."));
        }
    }
}

/// <summary>
/// Faixas de <c>Resiliencia:*</c> e validação cruzada (Princípio II): a soma das esperas de retentativa
/// <c>Σ min(AtrasoBaseMs·2^i, AtrasoMaximoMs)</c> precisa caber em <c>IntervalSeconds</c>.
/// </summary>
internal sealed class ValidadorResiliencia(IConfiguration configuracao) : IValidateOptions<ResilienciaOptions>
{
    public ValidateOptionsResult Validate(string? name, ResilienciaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var falhas = new List<string>();
        var intervalo = ConfiguracaoServico.InterpretarIntervalo(configuracao["Settings:IntervalSeconds"]);
        Validar("WAccess", options.WAccess, intervalo, falhas);
        Validar("Arquivos", options.Arquivos, intervalo, falhas);
        Validar("Replicacao", options.Replicacao, intervalo, falhas);
        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }

    /// <summary>Soma das esperas de retentativa (sem jitter, pior caso).</summary>
    public static long SomaEsperasMs(PoliticaDependenciaOptions politica)
    {
        ArgumentNullException.ThrowIfNull(politica);
        long soma = 0;
        for (var i = 0; i < politica.MaxTentativas; i++)
        {
            soma += (long)Math.Min(politica.AtrasoBaseMs * Math.Pow(2, i), politica.AtrasoMaximoMs);
        }

        return soma;
    }

    private static void Validar(string dependencia, PoliticaDependenciaOptions p, int? intervalo, List<string> falhas)
    {
        var prefixo = $"Resiliencia:{dependencia}:";
        ValidadorProcessamento.Faixa(p.MaxTentativas, 0, 10, prefixo + "MaxTentativas", falhas);
        ValidadorProcessamento.Faixa(p.AtrasoBaseMs, 100, 60000, prefixo + "AtrasoBaseMs", falhas);
        if (p.AtrasoMaximoMs < p.AtrasoBaseMs)
        {
            falhas.Add($"{prefixo}AtrasoMaximoMs deve ser maior ou igual a {prefixo}AtrasoBaseMs.");
        }

        ValidadorProcessamento.Faixa(p.TimeoutConexaoSegundos, 1, 120, prefixo + "TimeoutConexaoSegundos", falhas);
        ValidadorProcessamento.Faixa(p.TimeoutComandoSegundos, 1, 300, prefixo + "TimeoutComandoSegundos", falhas);
        ValidadorProcessamento.Faixa(p.TimeoutOperacaoSegundos, 1, 300, prefixo + "TimeoutOperacaoSegundos", falhas);
        ValidadorProcessamento.Faixa(p.Circuito.TaxaFalha, 0.1, 1.0, prefixo + "Circuito:TaxaFalha", falhas);
        ValidadorProcessamento.Faixa(p.Circuito.JanelaSegundos, 5, 600, prefixo + "Circuito:JanelaSegundos", falhas);
        ValidadorProcessamento.Faixa(p.Circuito.MinimoChamadas, 2, 100, prefixo + "Circuito:MinimoChamadas", falhas);
        ValidadorProcessamento.Faixa(p.Circuito.PausaSegundos, 5, 3600, prefixo + "Circuito:PausaSegundos", falhas);

        if (intervalo is { } segundos && SomaEsperasMs(p) > segundos * 1000L)
        {
            falhas.Add(FormattableString.Invariant(
                $"{prefixo}MaxTentativas/AtrasoBaseMs/AtrasoMaximoMs: a soma das esperas de retentativa ({SomaEsperasMs(p)} ms) excede Settings:IntervalSeconds ({segundos} s)."));
        }
    }
}
