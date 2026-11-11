using System.Globalization;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using Microsoft.Extensions.Options;

namespace InativacaoUsuarioInter.Service.Configuracao;

/// <summary>
/// Configuração resolvida e tipada, construída a partir das options já validadas.
/// Nunca expõe a string de conexão em <see cref="ToString"/>.
/// </summary>
public sealed class ConfiguracaoServico
{
    public required Instancia Instancia { get; init; }

    public required int IntervaloSegundos { get; init; }

    public string? RaizInter { get; init; }

    public required string RaizCondominio { get; init; }

    public required string ConexaoWAccess { get; init; }

    public required string DiretorioEstado { get; init; }

    public required ProcessamentoOptions Processamento { get; init; }

    public required ResilienciaOptions Resiliencia { get; init; }

    /// <summary>Raiz de pastas da instância corrente.</summary>
    public string RaizInstancia => Instancia == Instancia.Inter ? RaizInter! : RaizCondominio;

    public TimeSpan Intervalo => TimeSpan.FromSeconds(IntervaloSegundos);

    public static ConfiguracaoServico Criar(
        IOptions<SettingsOptions> settings,
        IOptions<SharePointFolderOptions> pastas,
        IOptions<ConnectionStringOptions> conexao,
        IOptions<ProcessamentoOptions> processamento,
        IOptions<ResilienciaOptions> resiliencia)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pastas);
        ArgumentNullException.ThrowIfNull(conexao);
        ArgumentNullException.ThrowIfNull(processamento);
        ArgumentNullException.ThrowIfNull(resiliencia);

        var instancia = InterpretarInstancia(settings.Value.Instancia)
            ?? throw new InvalidOperationException("Settings:Instancia inválida.");
        var intervalo = InterpretarIntervalo(settings.Value.IntervalSeconds)
            ?? throw new InvalidOperationException("Settings:IntervalSeconds inválido.");

        var proc = processamento.Value;
        var diretorioEstado = string.IsNullOrWhiteSpace(proc.DiretorioEstado)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "InativacaoUsuarioInter",
                instancia.ToString())
            : proc.DiretorioEstado;

        return new ConfiguracaoServico
        {
            Instancia = instancia,
            IntervaloSegundos = intervalo,
            RaizInter = pastas.Value.FolderPathInter,
            RaizCondominio = pastas.Value.FolderPathCondominio
                ?? throw new InvalidOperationException("SharePointFolder:FolderPathCondominio ausente."),
            ConexaoWAccess = conexao.Value.WAccess
                ?? throw new InvalidOperationException("ConnectionString:WAccess ausente."),
            DiretorioEstado = diretorioEstado,
            Processamento = proc,
            Resiliencia = resiliencia.Value,
        };
    }

    /// <summary>Interpreta <c>Settings:Instancia</c> sem diferenciar maiúsculas.</summary>
    public static Instancia? InterpretarInstancia(string? valor)
    {
        if (string.Equals(valor?.Trim(), nameof(Instancia.Inter), StringComparison.OrdinalIgnoreCase))
        {
            return Instancia.Inter;
        }

        if (string.Equals(valor?.Trim(), nameof(Instancia.Condominio), StringComparison.OrdinalIgnoreCase))
        {
            return Instancia.Condominio;
        }

        return null;
    }

    /// <summary>Interpreta <c>Settings:IntervalSeconds</c>; aceita somente inteiros entre 10 e 21600.</summary>
    public static int? InterpretarIntervalo(string? valor)
    {
        if (!int.TryParse(valor?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var segundos))
        {
            return null;
        }

        return segundos is >= LimitesConfiguracao.IntervaloMinimo and <= LimitesConfiguracao.IntervaloMaximo
            ? segundos
            : null;
    }

    public override string ToString() =>
        $"Instancia={Instancia}, IntervaloSegundos={IntervaloSegundos}";
}

/// <summary>Limites de configuração (FR-005, contracts/configuracao.md).</summary>
public static class LimitesConfiguracao
{
    public const int IntervaloMinimo = 10;
    public const int IntervaloMaximo = 21600;
}
