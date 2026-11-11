using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Configuracao;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Registros;

namespace InativacaoUsuarioInter.Service.Entrada;

/// <summary>Resultado da leitura de um arquivo: aceito (<see cref="Arquivo"/>) ou rejeitado (<see cref="CausaRejeicao"/>).</summary>
public sealed record ResultadoLeitura(
    string Caminho,
    string Nome,
    FormatoArquivo Formato,
    string Hash,
    string HashRaiz,
    string BaseRaiz,
    bool EhParteConhecida,
    ArquivoEntrada? Arquivo,
    string? CausaRejeicao);

/// <summary>
/// Lê, decodifica, interpreta e valida um arquivo de entrada (FR-008–FR-013).
/// Limita tamanho e quantidade antes de carregar; resolve o <c>HashRaiz</c> no diário
/// <b>antes</b> de calcular as chaves de idempotência (research R-08, análise H4).
/// </summary>
internal sealed class LeitorArquivo(IArmazenamentoEstado estado, ConfiguracaoServico configuracao)
{
    private static readonly UTF8Encoding Utf8Estrito = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static FormatoArquivo? FormatoPorExtensao(string caminho) =>
        Path.GetExtension(caminho).ToUpperInvariant() switch
        {
            ".JSON" => FormatoArquivo.Json,
            ".TXT" => FormatoArquivo.Txt,
            _ => null,
        };

    public async Task<ResultadoLeitura> LerAsync(string caminho, CancellationToken cancelamento)
    {
        var nome = Path.GetFileName(caminho);
        var formato = FormatoPorExtensao(caminho) ?? throw new ArgumentException("Extensão não suportada.", nameof(caminho));
        var tamanho = new FileInfo(caminho).Length;
        var limite = configuracao.Processamento;

        if (tamanho > limite.TamanhoMaximoBytes)
        {
            var hashGrande = await HashConteudo.CalcularArquivoAsync(caminho, cancelamento);
            return await RejeitarAsync(caminho, nome, formato, hashGrande, CausasFalha.ArquivoExcedeLimite, cancelamento);
        }

        var bytes = await File.ReadAllBytesAsync(caminho, cancelamento);
        var hash = HashConteudo.Calcular(bytes);
        var origem = await estado.ResolverHashRaizAsync(hash, cancelamento);
        var hashRaiz = origem?.HashRaiz ?? hash;
        var baseRaiz = origem?.BaseRaiz ?? Path.GetFileNameWithoutExtension(nome);

        string texto;
        try
        {
            var inicio = bytes.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
            texto = Utf8Estrito.GetString(bytes, inicio, bytes.Length - inicio);
        }
        catch (DecoderFallbackException)
        {
            return Rejeicao(caminho, nome, formato, hash, hashRaiz, baseRaiz, origem is not null, CausasFalha.ArquivoInvalido);
        }

        var parse = formato == FormatoArquivo.Json ? ParserJson.Interpretar(texto) : ParserTxt.Interpretar(texto);
        if (!parse.Valido || parse.Registros.Count == 0)
        {
            return Rejeicao(caminho, nome, formato, hash, hashRaiz, baseRaiz, origem is not null, CausasFalha.ArquivoInvalido);
        }

        if (parse.Registros.Count > limite.MaximoRegistros)
        {
            return Rejeicao(caminho, nome, formato, hash, hashRaiz, baseRaiz, origem is not null, CausasFalha.ArquivoExcedeLimite);
        }

        var registros = ValidadorRegistro.Validar(parse.Registros, configuracao.Instancia, hashRaiz);
        var arquivo = new ArquivoEntrada(caminho, nome, formato, hash, hashRaiz, baseRaiz, tamanho, registros);
        return new ResultadoLeitura(caminho, nome, formato, hash, hashRaiz, baseRaiz, origem is not null, arquivo, null);
    }

    private async Task<ResultadoLeitura> RejeitarAsync(
        string caminho, string nome, FormatoArquivo formato, string hash, string causa, CancellationToken cancelamento)
    {
        var origem = await estado.ResolverHashRaizAsync(hash, cancelamento);
        return Rejeicao(
            caminho, nome, formato, hash, origem?.HashRaiz ?? hash,
            origem?.BaseRaiz ?? Path.GetFileNameWithoutExtension(nome), origem is not null, causa);
    }

    private static ResultadoLeitura Rejeicao(
        string caminho, string nome, FormatoArquivo formato, string hash, string hashRaiz, string baseRaiz, bool parte, string causa) =>
        new(caminho, nome, formato, hash, hashRaiz, baseRaiz, parte, null, causa);
}
