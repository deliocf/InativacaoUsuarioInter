using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using InativacaoUsuarioInter.Service.Inativacao;

namespace InativacaoUsuarioInter.Service.Telemetria;

/// <summary>
/// Fonte de rastreamento e medidor OpenTelemetry do serviço (Princípio IV, research R-14).
/// Nenhum atributo, tag ou valor pode conter CPF ou nome: a correlação usa a chave de idempotência (hash).
/// </summary>
internal static class Instrumentacao
{
    public const string Nome = "InativacaoUsuarioInter";

    public static readonly ActivitySource Fonte = new(Nome);

    public static readonly Meter Medidor = new(Nome);

    public static readonly Counter<long> RegistrosProcessados =
        Medidor.CreateCounter<long>("registros_processados", description: "Registros processados por resultado.");

    public static readonly Histogram<double> DuracaoRegistroMs =
        Medidor.CreateHistogram<double>("duracao_registro_ms", "ms", "Duração do processamento de um registro.");

    public static readonly Histogram<double> DuracaoArquivoMs =
        Medidor.CreateHistogram<double>("duracao_arquivo_ms", "ms", "Duração do processamento de um arquivo.");

    public static readonly Histogram<double> DuracaoCicloMs =
        Medidor.CreateHistogram<double>("duracao_ciclo_ms", "ms", "Duração de um ciclo de varredura.");

    public static readonly Counter<long> Retentativas =
        Medidor.CreateCounter<long>("retentativas", description: "Retentativas por dependência.");

    static Instrumentacao()
    {
        Medidor.CreateObservableGauge(
            "circuito_estado",
            () => EstadoMetricas.Circuitos.Select(c =>
                new Measurement<int>(c.Value, new KeyValuePair<string, object?>(Atributos.Dependencia, c.Key))),
            description: "0=Fechado, 1=Aberto, 2=MeioAberto.");
        Medidor.CreateObservableGauge(
            "backlog_pendente",
            () => Interlocked.Read(ref EstadoMetricas.BacklogPendente),
            description: "Registros pendentes por falha transitória.");
        Medidor.CreateObservableGauge(
            "idade_pendencia_mais_antiga_s",
            () => EstadoMetricas.IdadePendenciaMaisAntigaSegundos,
            "s",
            "Idade da pendência mais antiga.");
        Medidor.CreateObservableGauge(
            "idade_arquivo_entrada_mais_antigo_s",
            () => EstadoMetricas.IdadeArquivoEntradaMaisAntigoSegundos,
            "s",
            "Idade do arquivo mais antigo ainda não processado em Input (inclui adiados).");
    }

    /// <summary>Abre um span de domínio já com o atributo de instância.</summary>
    public static Activity? Iniciar(string nome, string instancia, ActivityKind tipo = ActivityKind.Internal, ActivityContext pai = default)
    {
        var atividade = Fonte.StartActivity(nome, tipo, pai);
        atividade?.SetTag(Atributos.Instancia, instancia);
        return atividade;
    }

    /// <summary>Converte o resultado do registro na classe exigida por <c>resultado</c> (FR-039).</summary>
    public static string ClasseResultado(ResultadoRegistro resultado) => resultado switch
    {
        ResultadoRegistro.Inativado or ResultadoRegistro.JaInativo => "sucesso",
        ResultadoRegistro.PendenteTransitorio => "transitoria",
        _ => "definitiva",
    };

    /// <summary>Nomes de atributos de domínio (FR-039).</summary>
    public static class Atributos
    {
        public const string Instancia = "instancia";
        public const string Arquivo = "arquivo";
        public const string ChaveIdempotencia = "idempotency_key";
        public const string Resultado = "resultado";
        public const string Tentativa = "tentativa";
        public const string LinhasAfetadas = "waccess.linhas_afetadas";

        /// <summary>Feature 002: linhas já inativas que só receberam <c>CHDownloadRequired = 1</c>.</summary>
        public const string LinhasMarcadasDownload = "waccess.linhas_marcadas_download";
        public const string Dependencia = "dependencia";
        public const string StatusDetalhado = "resultado.detalhe";
    }
}

/// <summary>Valores lidos pelos instrumentos observáveis; atualizados pelo publicador de saúde.</summary>
internal static class EstadoMetricas
{
    public static readonly ConcurrentDictionary<string, int> Circuitos = new(StringComparer.Ordinal);

#pragma warning disable CA2211 // Campos lidos com Interlocked/volatile pelos instrumentos observáveis.
    public static long BacklogPendente;
    public static double IdadePendenciaMaisAntigaSegundos;
    public static double IdadeArquivoEntradaMaisAntigoSegundos;
#pragma warning restore CA2211
}
