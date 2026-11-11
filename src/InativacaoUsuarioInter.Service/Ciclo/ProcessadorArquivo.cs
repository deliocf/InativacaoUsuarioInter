using System.Diagnostics;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Registros;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Service.Telemetria;
using Microsoft.Extensions.Logging;

namespace InativacaoUsuarioInter.Service.Ciclo;

/// <summary>Resumo do processamento de um arquivo (telemetria e testes).</summary>
public sealed record ResumoArquivo(string Nome, bool Novo, Finalizacao? Finalizacao, int Sucessos, int Falhas, int Pendentes);

/// <summary>
/// Processa um arquivo de entrada (FR-012–FR-029): leitura e validação → diário → para cada registro válido
/// e não terminal: pré-leitura → intenção + estado anterior → UPDATE (se Ativo) → resultado + linha de log →
/// log → marca. Registros terminais só têm o log garantido (retomada sem refazer efeitos, R-08).
/// </summary>
internal sealed partial class ProcessadorArquivo(
    LeitorArquivo leitor,
    IArmazenamentoEstado estado,
    IRepositorioWAccess repositorio,
    IRegistroLogs logs,
    IGerenciadorArquivos gerenciador,
    ConfiguracaoServico configuracao,
    EstadoDependenciasCiclo dependencias,
    ProvedorPipelines pipelines,
    IPontoFalha pontoFalha,
    LeitorTraceparent leitorTraceparent,
    TimeProvider relogio,
    ILogger<ProcessadorArquivo> logger)
{
    private readonly Instancia _instancia = configuracao.Instancia;

    public async Task<ResumoArquivo> ProcessarAsync(string caminho, CancellationToken cancelamento)
    {
        var inicio = Stopwatch.GetTimestamp();
        var nome = Path.GetFileName(caminho);
        var pai = await leitorTraceparent.LerAsync(caminho, cancelamento);
        using var atividade = Instrumentacao.Iniciar("arquivo", _instancia.ToString(), pai: pai ?? default);
        atividade?.SetTag(Instrumentacao.Atributos.Arquivo, nome);
        var orcamento = dependencias.Orcamento;

        var leitura = await pipelines.ExecutarAsync(
            Dependencias.PastaLocal, ct => new ValueTask<ResultadoLeitura>(leitor.LerAsync(caminho, ct)), orcamento, cancelamento);
        atividade?.SetTag(Instrumentacao.Atributos.ChaveIdempotencia, leitura.Hash);
        var conhecido = await estado.ObterArquivoAsync(leitura.Hash, cancelamento);
        var novo = conhecido is null && !leitura.EhParteConhecida;

        if (conhecido is not null && EstadosArquivo.Terminal(conhecido.Estado))
        {
            // Mesmo conteúdo já concluído: no-op, apenas sai da entrada (Princípio III).
            await gerenciador.RemoverDuplicadoAsync(caminho, orcamento, cancelamento);
            LogDuplicado(logger, nome, leitura.Hash);
            atividade?.SetTag(Instrumentacao.Atributos.Resultado, "sucesso");
            return new ResumoArquivo(nome, false, null, 0, 0, 0);
        }

        if (pai is { } contextoPai)
        {
            await estado.RegistrarTraceparentAsync(leitura.Hash, LeitorTraceparent.Formatar(contextoPai), cancelamento);
        }

        if (leitura.CausaRejeicao is { } causa)
        {
            await RejeitarAsync(leitura, causa, conhecido, orcamento, cancelamento);
            atividade?.SetTag(Instrumentacao.Atributos.Resultado, "definitiva");
            Instrumentacao.DuracaoArquivoMs.Record(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            return new ResumoArquivo(nome, novo, null, 0, 1, 0);
        }

        var arquivo = leitura.Arquivo!;
        var traceparent = pai is { } p ? LeitorTraceparent.Formatar(p) : null;
        await estado.RegistrarArquivoAsync(
            new EstadoArquivo(
                arquivo.Hash, arquivo.HashRaiz, _instancia.ToString(), nome, arquivo.BaseRaiz, arquivo.Formato.ToString(),
                EstadosArquivo.EmProcessamento, null, null, false, traceparent),
            arquivo.Registros
                .Where(r => r.ChaveIdempotencia is not null)
                .Select(r => new NovoRegistro(r.ChaveIdempotencia!, r.Indice))
                .ToList(),
            cancelamento);
        var estados = await estado.ObterRegistrosAsync(arquivo.HashRaiz, cancelamento);

        var sucessos = new List<RegistroEntrada>();
        var falhas = new List<RegistroEntrada>();
        var pendentes = new List<RegistroEntrada>();
        foreach (var registro in arquivo.Registros)
        {
            switch (registro.Validacao)
            {
                case ValidacaoRegistro.Duplicado:
                    LogRegistroDuplicado(logger, nome, registro.Indice);
                    continue;
                case ValidacaoRegistro.DadoInvalido:
                    await ProcessarInvalidoAsync(registro, estados[registro.ChaveIdempotencia!], nome, cancelamento);
                    falhas.Add(registro);
                    continue;
            }

            var resultado = await ProcessarValidoAsync(registro, estados[registro.ChaveIdempotencia!], nome, cancelamento);
            (resultado switch
            {
                ResultadoRegistro.Inativado or ResultadoRegistro.JaInativo => sucessos,
                ResultadoRegistro.PendenteTransitorio => pendentes,
                _ => falhas,
            }).Add(registro);
        }

        var finalizacao = await gerenciador.FinalizarAsync(
            leitura, new GruposFinalizacao(sucessos, falhas, pendentes), orcamento, cancelamento);
        atividade?.SetTag(
            Instrumentacao.Atributos.Resultado,
            pendentes.Count > 0 ? "transitoria" : falhas.Count > 0 ? "definitiva" : "sucesso");
        Instrumentacao.DuracaoArquivoMs.Record(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        LogArquivoProcessado(logger, nome, sucessos.Count, falhas.Count, pendentes.Count, finalizacao);
        return new ResumoArquivo(nome, novo, finalizacao, sucessos.Count, falhas.Count, pendentes.Count);
    }

    private async Task RejeitarAsync(
        ResultadoLeitura leitura, string causa, EstadoArquivo? conhecido, OrcamentoCiclo? orcamento, CancellationToken cancelamento)
    {
        var estadoArquivo = new EstadoArquivo(
            leitura.Hash, leitura.HashRaiz, _instancia.ToString(), leitura.Nome, leitura.BaseRaiz, leitura.Formato.ToString(),
            EstadosArquivo.EmProcessamento, causa, null, false, null);
        var linha = conhecido?.LinhaLog
            ?? FormatadorLinhaLog.FormatarFalha(relogio.GetLocalNow(), _instancia, leitura.Nome, null, causa);
        await estado.RegistrarRejeicaoAsync(estadoArquivo, causa, linha, cancelamento);
        var registrado = await estado.ObterArquivoAsync(leitura.Hash, cancelamento);

        if (registrado is { LogGravado: false, LinhaLog: { } linhaGravada })
        {
            await GarantirLogAsync(TipoLog.Falhas, linhaGravada, () => estado.MarcarLogArquivoGravadoAsync(leitura.Hash, cancelamento), cancelamento);
        }

        await gerenciador.RejeitarAsync(leitura, orcamento, cancelamento);
        Instrumentacao.RegistrosProcessados.Add(1, new KeyValuePair<string, object?>(Instrumentacao.Atributos.Resultado, "definitiva"));
        LogRejeitado(logger, leitura.Nome, causa);
    }

    private async Task ProcessarInvalidoAsync(RegistroEntrada registro, EstadoRegistro atual, string arquivo, CancellationToken cancelamento)
    {
        var chave = registro.ChaveIdempotencia!;
        using var atividade = IniciarSpanRegistro(arquivo, chave, atual.Tentativas + 1);
        var linha = atual.LinhaLog;
        if (atual.Estado != EstadosRegistro.DadoInvalido || linha is null)
        {
            linha = FormatadorLinhaLog.FormatarFalha(relogio.GetLocalNow(), _instancia, arquivo, registro.Cpf, CausasFalha.DadoInvalido);
            await estado.RegistrarResultadoAsync(chave, ResultadoRegistro.DadoInvalido, linha, null, relogio.GetUtcNow(), cancelamento);
            Contabilizar(ResultadoRegistro.DadoInvalido);
        }

        if (!atual.LogGravado)
        {
            await GarantirLogAsync(TipoLog.Falhas, linha, () => estado.MarcarLogGravadoAsync(chave, cancelamento), cancelamento);
        }

        atividade?.SetTag(Instrumentacao.Atributos.Resultado, "definitiva");
    }

    private async Task<ResultadoRegistro> ProcessarValidoAsync(
        RegistroEntrada registro, EstadoRegistro atual, string arquivo, CancellationToken cancelamento)
    {
        var chave = registro.ChaveIdempotencia!;
        var inicio = Stopwatch.GetTimestamp();
        using var atividade = IniciarSpanRegistro(arquivo, chave, atual.Tentativas + 1);

        if (EstadosRegistro.Terminal(atual.Estado))
        {
            var terminal = EstadosRegistro.ParaResultado(atual.Estado)!.Value;
            if (!atual.LogGravado && atual.LinhaLog is { } pendente)
            {
                await GarantirLogAsync(TipoDeLog(terminal), pendente, () => estado.MarcarLogGravadoAsync(chave, cancelamento), cancelamento);
            }

            atividade?.SetTag(Instrumentacao.Atributos.Resultado, Instrumentacao.ClasseResultado(terminal));
            return terminal;
        }

        if (dependencias.Indisponivel(Dependencias.WAccess))
        {
            return await RegistrarPendenciaAsync(chave, atividade, cancelamento);
        }

        var orcamento = dependencias.Orcamento;
        EstadoAnterior anterior;
        ResultadoInativacao? passoB;
        try
        {
            if (atual.Estado == EstadosRegistro.EmExecucao && atual.EstadoAnterior is { } gravado)
            {
                // Retomada: usa o estado anterior gravado; nunca o infere pela intenção (análise H1).
                anterior = gravado;
            }
            else
            {
                anterior = await repositorio.LerEstadoAsync(registro.Cpf!, orcamento, cancelamento);
                await estado.MarcarIntencaoAsync(chave, anterior, relogio.GetUtcNow(), cancelamento);
            }

            pontoFalha.Atingido(PontoFalha.AposIntencao);
            passoB = anterior == EstadoAnterior.Ativo
                ? await repositorio.InativarAsync(registro.Cpf!, orcamento, cancelamento)
                : null;
            pontoFalha.Atingido(PontoFalha.AposComandoWAccess);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            var erro = pipelines.Classificar(Dependencias.WAccess, excecao);
            dependencias.MarcarIndisponivel(Dependencias.WAccess, erro, relogio.GetUtcNow());
            LogFalhaWAccess(logger, chave, erro.Categoria, excecao.GetType().Name);
            return await RegistrarPendenciaAsync(chave, atividade, cancelamento);
        }

        var resultado = ClassificacaoWAccess.Classificar(anterior, passoB);
        if (passoB is { Inativados: > 1 } multiplos)
        {
            LogMultiplosCadastros(logger, chave, multiplos.Inativados);
        }

        var agora = relogio.GetLocalNow();
        var linha = resultado == ResultadoRegistro.CpfNaoEncontrado
            ? FormatadorLinhaLog.FormatarFalha(agora, _instancia, arquivo, registro.Cpf, CausasFalha.CpfNaoEncontrado)
            : FormatadorLinhaLog.FormatarAuditoria(
                agora, _instancia, arquivo, registro.Cpf!, registro.IdResponsavel!, registro.NomeResponsavel!, FormatadorLinhaLog.Status(resultado));
        await estado.RegistrarResultadoAsync(chave, resultado, linha, passoB?.Inativados, agora, cancelamento);
        pontoFalha.Atingido(PontoFalha.AposRegistrarResultado);
        await GarantirLogAsync(TipoDeLog(resultado), linha, () => estado.MarcarLogGravadoAsync(chave, cancelamento), cancelamento);

        Contabilizar(resultado);
        Instrumentacao.DuracaoRegistroMs.Record(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        atividade?.SetTag(Instrumentacao.Atributos.Resultado, Instrumentacao.ClasseResultado(resultado));
        atividade?.SetTag(Instrumentacao.Atributos.StatusDetalhado, resultado.ToString());
        return resultado;
    }

    private async Task<ResultadoRegistro> RegistrarPendenciaAsync(string chave, Activity? atividade, CancellationToken cancelamento)
    {
        await estado.RegistrarPendenciaAsync(chave, relogio.GetUtcNow(), cancelamento);
        Contabilizar(ResultadoRegistro.PendenteTransitorio);
        atividade?.SetTag(Instrumentacao.Atributos.Resultado, "transitoria");
        return ResultadoRegistro.PendenteTransitorio;
    }

    /// <summary>
    /// Anexa a linha e marca no diário. Se a pasta falhar, a linha continua pendente no diário e a
    /// recuperação a grava depois, exatamente uma vez (FR-021).
    /// </summary>
    private async Task GarantirLogAsync(TipoLog tipo, string linha, Func<Task> marcar, CancellationToken cancelamento)
    {
        try
        {
            await logs.AnexarAsync(tipo, linha, dependencias.Orcamento, cancelamento);
        }
        catch (Exception excecao) when (ClassificadorErros.Capturavel(excecao, cancelamento))
        {
            var erro = pipelines.Classificar(Dependencias.PastaLocal, excecao);
            dependencias.MarcarIndisponivel(Dependencias.PastaLocal, erro, relogio.GetUtcNow());
            LogFalhaLog(logger, tipo, erro.Categoria);
            return;
        }

        pontoFalha.Atingido(PontoFalha.AposAppendLog);
        await marcar();
    }

    private Activity? IniciarSpanRegistro(string arquivo, string chave, int tentativa)
    {
        var atividade = Instrumentacao.Iniciar("registro", _instancia.ToString());
        atividade?.SetTag(Instrumentacao.Atributos.Arquivo, arquivo);
        atividade?.SetTag(Instrumentacao.Atributos.ChaveIdempotencia, chave);
        atividade?.SetTag(Instrumentacao.Atributos.Tentativa, tentativa);
        return atividade;
    }

    private static TipoLog TipoDeLog(ResultadoRegistro resultado) =>
        resultado is ResultadoRegistro.Inativado or ResultadoRegistro.JaInativo ? TipoLog.Auditoria : TipoLog.Falhas;

    private static void Contabilizar(ResultadoRegistro resultado) =>
        Instrumentacao.RegistrosProcessados.Add(
            1, new KeyValuePair<string, object?>(Instrumentacao.Atributos.Resultado, Instrumentacao.ClasseResultado(resultado)));

    [LoggerMessage(Level = LogLevel.Information, Message = "Arquivo {Arquivo} já processado (hash {Hash}); duplicado removido da entrada.")]
    private static partial void LogDuplicado(ILogger logger, string arquivo, string hash);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Arquivo {Arquivo}: registro {Indice} repete CPF já presente; processado uma única vez.")]
    private static partial void LogRegistroDuplicado(ILogger logger, string arquivo, int indice);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arquivo {Arquivo} rejeitado: {Causa}.")]
    private static partial void LogRejeitado(ILogger logger, string arquivo, string causa);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arquivo {Arquivo}: {Sucessos} sucesso(s), {Falhas} falha(s) definitiva(s), {Pendentes} pendente(s); {Finalizacao}.")]
    private static partial void LogArquivoProcessado(ILogger logger, string arquivo, int sucessos, int falhas, int pendentes, Finalizacao finalizacao);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WAccess indisponível ({Categoria}, {Tipo}); registro {ChaveIdempotencia} fica pendente.")]
    private static partial void LogFalhaWAccess(ILogger logger, string chaveIdempotencia, CategoriaErro categoria, string tipo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CpfComMultiplosCadastros: registro {ChaveIdempotencia} inativou {Quantidade} cadastros (research R-02).")]
    private static partial void LogMultiplosCadastros(ILogger logger, string chaveIdempotencia, int quantidade);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao gravar log de {Tipo} ({Categoria}); linha preservada no diário para nova tentativa.")]
    private static partial void LogFalhaLog(ILogger logger, TipoLog tipo, CategoriaErro categoria);
}
