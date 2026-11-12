using InativacaoUsuarioInter.Service.Inativacao;

namespace InativacaoUsuarioInter.Service.Estado;

/// <summary>Estados de arquivo (data-model §3).</summary>
public static class EstadosArquivo
{
    public const string EmProcessamento = "EmProcessamento";
    public const string Concluido = "Concluido";
    public const string Segregado = "Segregado";
    public const string Rejeitado = "Rejeitado";

    public static bool Terminal(string estado) =>
        estado is Concluido or Segregado or Rejeitado;
}

/// <summary>Estados de registro (data-model §2–3).</summary>
public static class EstadosRegistro
{
    public const string Pendente = "Pendente";
    public const string EmExecucao = "EmExecucao";
    public const string Inativado = "Inativado";
    public const string JaInativo = "JaInativo";
    public const string CpfNaoEncontrado = "CpfNaoEncontrado";
    public const string DadoInvalido = "DadoInvalido";

    public static bool Terminal(string estado) =>
        estado is Inativado or JaInativo or CpfNaoEncontrado or DadoInvalido;

    public static string De(ResultadoRegistro resultado) => resultado switch
    {
        ResultadoRegistro.Inativado => Inativado,
        ResultadoRegistro.JaInativo => JaInativo,
        ResultadoRegistro.CpfNaoEncontrado => CpfNaoEncontrado,
        ResultadoRegistro.DadoInvalido => DadoInvalido,
        _ => Pendente,
    };

    public static ResultadoRegistro? ParaResultado(string estado) => estado switch
    {
        Inativado => ResultadoRegistro.Inativado,
        JaInativo => ResultadoRegistro.JaInativo,
        CpfNaoEncontrado => ResultadoRegistro.CpfNaoEncontrado,
        DadoInvalido => ResultadoRegistro.DadoInvalido,
        _ => null,
    };
}

/// <summary>Tipos de parte gerada (data-model §2, R-10).</summary>
public static class TiposParte
{
    public const string Integral = "Integral";
    public const string Sucesso = "Sucesso";
    public const string Falhas = "Falhas";
    public const string Pendente = "Pendente";
    public const string Rejeitado = "Rejeitado";
}

/// <summary>Destino de uma linha de log contratual.</summary>
public enum TipoLog
{
    Auditoria,
    Falhas,
}

public sealed record EstadoArquivo(
    string Hash,
    string HashRaiz,
    string Instancia,
    string NomeOriginal,
    string BaseRaiz,
    string Formato,
    string Estado,
    string? CausaRejeicao,
    string? LinhaLog,
    bool LogGravado,
    string? Traceparent);

public sealed record EstadoRegistro(
    string Chave,
    string HashArquivo,
    string HashRaiz,
    int Indice,
    string Estado,
    EstadoAnterior? EstadoAnterior,
    int Tentativas,
    DateTimeOffset? PendenteDesde,
    string? LinhaLog,
    bool LogGravado);

public sealed record NovoRegistro(string Chave, int Indice);

public sealed record ParteGerada(string Hash, string HashOrigem, string Tipo, string CaminhoFinal);

public sealed record OrigemArquivo(string HashRaiz, string BaseRaiz);

public sealed record LogPendente(string Identificador, bool EhArquivo, TipoLog Tipo, string Linha);

/// <summary>
/// Entrega pendente: <paramref name="NomeOutput"/> é o arquivo na fila <c>Output</c>; <paramref name="NomeDestino"/> é o
/// nome com que ele deve aparecer no Condomínio (o nome original; <c>null</c> em entregas antigas = <c>NomeOutput</c>).
/// </summary>
public sealed record EntregaPendente(string Hash, string NomeOutput, int Tentativas, string? NomeDestino = null);

/// <summary>Estados de entrega Inter → Condomínio (tabela <c>entrega</c>).</summary>
public static class EstadosEntrega
{
    public const string NaoEntregue = "NaoEntregue";
    public const string Entregue = "Entregue";
}

/// <summary>Entrega registrada no diário (feature 002, research R-04).</summary>
public sealed record EntregaRegistrada(string Hash, string Estado, string NomeOutput, string? NomeDestino);

/// <summary>
/// Diário durável do módulo Estado (research R-07/R-08). Apenas este módulo acessa o arquivo SQLite.
/// </summary>
public interface IArmazenamentoEstado
{
    Task InicializarAsync(CancellationToken cancelamento);

    Task<EstadoArquivo?> ObterArquivoAsync(string hash, CancellationToken cancelamento);

    /// <summary>
    /// Parte com o conteúdo <paramref name="hash"/>. Se arquivos de origem diferentes geraram o mesmo conteúdo, prefere
    /// uma parte entregável (<c>Integral</c>/<c>Sucesso</c>) e, entre elas, a mais recente.
    /// </summary>
    Task<ParteGerada?> ObterParteAsync(string hash, CancellationToken cancelamento);

    /// <summary>Se <paramref name="hash"/> é uma parte <c>__pendente-</c> conhecida, devolve a origem (hash raiz e base raiz).</summary>
    Task<OrigemArquivo?> ResolverHashRaizAsync(string hash, CancellationToken cancelamento);

    /// <summary>
    /// Identidade do arquivo <paramref name="nome"/> com conteúdo <paramref name="hashConteudo"/> no diário:
    /// <see cref="Arquivos.HashConteudo.ChaveArquivo"/>, ou o próprio hash do conteúdo se o diário já tem esse arquivo
    /// (mesmo nome e conteúdo) registrado pela versão anterior, que usava só o conteúdo como identidade.
    /// </summary>
    Task<string> ResolverChaveArquivoAsync(string nome, string hashConteudo, CancellationToken cancelamento);

    /// <summary>Registra o arquivo (se novo) e seus registros (os já existentes passam a apontar para este arquivo).</summary>
    Task RegistrarArquivoAsync(EstadoArquivo arquivo, IReadOnlyCollection<NovoRegistro> registros, CancellationToken cancelamento);

    Task<IReadOnlyDictionary<string, EstadoRegistro>> ObterRegistrosAsync(string hashRaiz, CancellationToken cancelamento);

    /// <summary>Grava <c>EmExecucao</c> + <c>estado_anterior</c> numa transação, antes de qualquer alteração no WAccess.</summary>
    Task MarcarIntencaoAsync(string chave, EstadoAnterior estadoAnterior, DateTimeOffset agora, CancellationToken cancelamento);

    /// <summary>Grava o resultado terminal e a linha de log pré-formatada numa transação.</summary>
    Task RegistrarResultadoAsync(string chave, ResultadoRegistro resultado, string linhaLog, int? linhasAfetadas, DateTimeOffset agora, CancellationToken cancelamento);

    Task MarcarLogGravadoAsync(string chave, CancellationToken cancelamento);

    /// <summary>Incrementa tentativas e fixa <c>pendente_desde</c> na primeira falha transitória.</summary>
    Task RegistrarPendenciaAsync(string chave, DateTimeOffset agora, CancellationToken cancelamento);

    Task RegistrarRejeicaoAsync(EstadoArquivo arquivo, string causa, string linhaLog, CancellationToken cancelamento);

    Task MarcarLogArquivoGravadoAsync(string hash, CancellationToken cancelamento);

    /// <summary>
    /// Registra a parte gerada e, se <paramref name="nomeEntrega"/> não for nulo, a entrega pendente com esse nome
    /// na fila <c>Output</c> (mesma transação).
    /// </summary>
    /// <remarks><paramref name="nomeDestino"/>: nome com que a entrega deve aparecer no Condomínio (padrão: <paramref name="nomeEntrega"/>).</remarks>
    Task RegistrarParteAsync(ParteGerada parte, string? nomeEntrega, CancellationToken cancelamento, string? nomeDestino = null);

    Task ConcluirArquivoAsync(string hash, string estado, DateTimeOffset agora, CancellationToken cancelamento);

    Task RegistrarTraceparentAsync(string hash, string traceparent, CancellationToken cancelamento);

    Task<IReadOnlyList<LogPendente>> ListarLogsPendentesAsync(CancellationToken cancelamento);

    Task<DateTimeOffset?> ObterPendenciaMaisAntigaAsync(CancellationToken cancelamento);

    Task<int> ContarPendenciasAnterioresAsync(DateTimeOffset limite, CancellationToken cancelamento);

    Task<int> ContarBacklogAsync(CancellationToken cancelamento);

    Task GarantirEntregaAsync(string hash, string nomeOutput, CancellationToken cancelamento);

    Task<IReadOnlyList<EntregaPendente>> ListarEntregasPendentesAsync(CancellationToken cancelamento);

    Task RegistrarEntregaAsync(string hash, string nomeOutput, string nomeDestino, string? traceparent, DateTimeOffset agora, CancellationToken cancelamento);

    Task RegistrarFalhaEntregaAsync(string hash, string nomeOutput, string categoria, CancellationToken cancelamento);

    Task<int> ContarEntregasPendentesAsync(CancellationToken cancelamento);

    /// <summary>
    /// Entrega registrada para o arquivo <paramref name="nomeOutput"/> da fila com conteúdo <paramref name="hash"/>, ou
    /// <c>null</c> (reconciliação de <c>Output</c>, research R-04). O mesmo conteúdo com outro nome é outra entrega.
    /// </summary>
    Task<EntregaRegistrada?> ObterEntregaAsync(string hash, string nomeOutput, CancellationToken cancelamento);

    /// <summary>Menor <c>registrada_em</c> entre as entregas <c>NaoEntregue</c> (alerta R-07), ou <c>null</c>.</summary>
    Task<DateTimeOffset?> ObterEntregaPendenteMaisAntigaAsync(CancellationToken cancelamento);

    /// <summary>Hash já calculado de um arquivo de <c>Output</c> com o mesmo nome, tamanho e data de escrita (T098).</summary>
    Task<string?> ObterHashOutputAsync(string nome, long tamanho, DateTimeOffset escritoEm, CancellationToken cancelamento);

    Task RegistrarHashOutputAsync(string nome, long tamanho, DateTimeOffset escritoEm, string hash, CancellationToken cancelamento);
}
