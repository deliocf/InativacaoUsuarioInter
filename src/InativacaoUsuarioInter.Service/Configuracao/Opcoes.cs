namespace InativacaoUsuarioInter.Service.Configuracao;

// Classes de options apenas com binding (contracts/configuracao.md).
// A validação está em ValidadoresConfiguracao.cs (US2).

/// <summary>Seção <c>Settings</c>. Valores lidos como texto para permitir mensagens de erro claras.</summary>
public sealed class SettingsOptions
{
    public const string Secao = "Settings";

    public string? Instancia { get; set; }

    public string? IntervalSeconds { get; set; }
}

/// <summary>Seção <c>SharePointFolder</c>.</summary>
public sealed class SharePointFolderOptions
{
    public const string Secao = "SharePointFolder";

    public string? FolderPathInter { get; set; }

    public string? FolderPathCondominio { get; set; }
}

/// <summary>Seção <c>ConnectionString</c> (singular). Apenas <c>WAccess</c> é lida (Clarification Q1).</summary>
public sealed class ConnectionStringOptions
{
    public const string Secao = "ConnectionString";

    public string? WAccess { get; set; }
}

/// <summary>Seção <c>Processamento</c>.</summary>
public sealed class ProcessamentoOptions
{
    public const string Secao = "Processamento";

    public string? DiretorioEstado { get; set; }

    public int EstabilizacaoSegundos { get; set; } = 5;

    public int MaximoRegistros { get; set; } = 1000;

    public long TamanhoMaximoBytes { get; set; } = 1_048_576;

    public int AlertaPendenciaHoras { get; set; } = 24;

    /// <summary>Espera máxima por bloqueio do diário SQLite (<c>PRAGMA busy_timeout</c>).</summary>
    public int TimeoutDiarioMs { get; set; } = 5000;
}

/// <summary>Parâmetros do circuit breaker de uma dependência.</summary>
public sealed class CircuitoOptions
{
    public double TaxaFalha { get; set; } = 0.5;

    public int JanelaSegundos { get; set; } = 30;

    public int MinimoChamadas { get; set; } = 3;

    public int PausaSegundos { get; set; } = 60;
}

/// <summary>Política de resiliência de uma dependência (research R-06).</summary>
public sealed class PoliticaDependenciaOptions
{
    public int MaxTentativas { get; set; } = 3;

    public int AtrasoBaseMs { get; set; } = 1000;

    public int AtrasoMaximoMs { get; set; } = 4000;

    public int TimeoutConexaoSegundos { get; set; } = 15;

    public int TimeoutComandoSegundos { get; set; } = 30;

    public int TimeoutOperacaoSegundos { get; set; } = 30;

    public CircuitoOptions Circuito { get; set; } = new();
}

/// <summary>Seção <c>Resiliencia</c>, uma política por dependência.</summary>
public sealed class ResilienciaOptions
{
    public const string Secao = "Resiliencia";

    public PoliticaDependenciaOptions WAccess { get; set; } = new();

    public PoliticaDependenciaOptions Arquivos { get; set; } = new();

    public PoliticaDependenciaOptions Replicacao { get; set; } = new();
}

/// <summary>Seção <c>Telemetria</c>.</summary>
public sealed class TelemetriaOptions
{
    public const string Secao = "Telemetria";

    public string? OtlpEndpoint { get; set; }
}
