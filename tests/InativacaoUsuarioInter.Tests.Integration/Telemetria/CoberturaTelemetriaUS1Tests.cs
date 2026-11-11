using InativacaoUsuarioInter.Service.Telemetria;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Integration.Telemetria;

/// <summary>T035 — toda operação de domínio da US1 tem span com atributos obrigatórios e nenhum CPF (Princípio IV).</summary>
public sealed class CoberturaTelemetriaUS1Tests(SqlServerFixture sql)
{
    private static readonly string[] Cpfs = ["22222222222", "33333333333", "44444444444"];

    [Fact]
    public async Task Ciclo_arquivo_e_registro_tem_spans_com_atributos_e_sem_dados_pessoais()
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetria = new ColetorTelemetria();
        await using var cenario = await Cenario.CriarAsync(sql, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync(Cpfs[0], 0, cancelamento: ct);
        await cenario.Banco.InserirUsuarioAsync(Cpfs[1], 1, cancelamento: ct);
        cenario.Ambiente.Depositar("a.txt", $"{Cpfs[0]},1,Maria Eduarda\r\n{Cpfs[1]},2,Carlos\r\n{Cpfs[2]},3,Luana\r\nxx,4,Invalido\r\n");

        await cenario.CicloAsync(ct);
        telemetria.Coletar();

        var spans = telemetria.Spans;
        var ciclo = Assert.Single(spans, s => s.DisplayName == "ciclo");
        Assert.Equal("Inter", ciclo.GetTagItem(Instrumentacao.Atributos.Instancia));

        var arquivo = Assert.Single(spans, s => s.DisplayName == "arquivo");
        AssertTags(arquivo, Instrumentacao.Atributos.Instancia, Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia, Instrumentacao.Atributos.Resultado);
        Assert.Equal(ciclo.TraceId, arquivo.TraceId);

        var registros = spans.Where(s => s.DisplayName == "registro").ToList();
        Assert.Equal(4, registros.Count);
        Assert.All(registros, r => AssertTags(
            r,
            Instrumentacao.Atributos.Instancia, Instrumentacao.Atributos.Arquivo, Instrumentacao.Atributos.ChaveIdempotencia,
            Instrumentacao.Atributos.Resultado, Instrumentacao.Atributos.Tentativa));
        Assert.Equal(
            ["definitiva", "definitiva", "sucesso", "sucesso"],
            registros.Select(r => (string)r.GetTagItem(Instrumentacao.Atributos.Resultado)!).Order(StringComparer.Ordinal));

        Assert.Contains(spans, s => s.DisplayName == "waccess.inativar");
        Assert.Equal(3, spans.Count(s => s.DisplayName == "waccess.ler_estado"));

        var textoSpans = string.Join('\n', spans.SelectMany(s => s.TagObjects.Select(t => $"{s.DisplayName}:{t.Key}={t.Value}")));
        var textoLogs = cenario.Ambiente.Logs.TextoCompleto();
        foreach (var dado in Cpfs.Concat(["Maria Eduarda", "Carlos", "Luana"]))
        {
            Assert.DoesNotContain(dado, textoSpans, StringComparison.Ordinal);
            Assert.DoesNotContain(dado, textoLogs, StringComparison.Ordinal);
        }

        Assert.True(telemetria.Somar("registros_processados", Instrumentacao.Atributos.Resultado, "sucesso") >= 2);
    }

    private static void AssertTags(System.Diagnostics.Activity span, params string[] tags)
    {
        foreach (var tag in tags)
        {
            Assert.True(span.GetTagItem(tag) is not null, $"Span '{span.DisplayName}' sem o atributo '{tag}'.");
        }
    }
}
