using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Tests.Unit.Entrada;

/// <summary>T031 — normalização do CPF (FR-011, Clarification 2026-11-11).</summary>
public sealed class NormalizadorCpfTests
{
    [Theory]
    [InlineData("01234567890", "01234567890")]
    [InlineData("012.345.678-90", "01234567890")]
    [InlineData(" 012 345 678 90 ", "01234567890")]
    [InlineData("22222222222", "22222222222")]
    public void Remove_apenas_ponto_hifen_e_espacos(string entrada, string esperado) =>
        Assert.Equal(esperado, NormalizadorCpf.Normalizar(entrada));

    [Theory]
    [InlineData("0123456789")]
    [InlineData("012345678901")]
    [InlineData("012/345/678-90")]
    [InlineData("cpf")]
    [InlineData("1'; DROP TABLE CHMain;--")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("０１２３４５６７８９０")]
    public void Valor_fora_do_formato_eh_invalido(string? entrada) =>
        Assert.Null(NormalizadorCpf.Normalizar(entrada));
}

/// <summary>T031 — JSON (FR-009).</summary>
public sealed class ParserJsonTests
{
    [Fact]
    public void Le_array_de_objetos_e_preserva_texto_bruto()
    {
        var resultado = ParserJson.Interpretar("""[ { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda", "extra": 5 } ]""");

        Assert.True(resultado.Valido);
        var registro = Assert.Single(resultado.Registros);
        Assert.True(registro.EstruturaValida);
        Assert.Equal("22222222222", registro.Cpf);
        Assert.Equal("Maria Eduarda", registro.NomeResponsavel);
        Assert.Contains("\"extra\": 5", registro.TextoBruto, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[ { "cpf": 22222222222, "id_responsavel": "1", "nome_responsavel": "Maria" } ]""")]
    [InlineData("""[ { "cpf": "22222222222", "nome_responsavel": "Maria" } ]""")]
    [InlineData("""[ { "cpf": "22222222222", "id_responsavel": null, "nome_responsavel": "Maria" } ]""")]
    [InlineData("""[ "22222222222" ]""")]
    public void Propriedade_nao_string_ou_ausente_invalida_o_registro(string json)
    {
        var resultado = ParserJson.Interpretar(json);

        Assert.True(resultado.Valido);
        Assert.False(Assert.Single(resultado.Registros).EstruturaValida);
    }

    [Theory]
    [InlineData("{ \"cpf\": \"22222222222\" }")]
    [InlineData("[ { \"cpf\": ")]
    [InlineData("não é json")]
    public void Raiz_que_nao_eh_array_ou_json_invalido_rejeita_o_arquivo(string json) =>
        Assert.False(ParserJson.Interpretar(json).Valido);
}

/// <summary>T031 — TXT (FR-010).</summary>
public sealed class ParserTxtTests
{
    [Fact]
    public void Divide_nas_duas_primeiras_virgulas_e_o_resto_eh_o_nome()
    {
        var resultado = ParserTxt.Interpretar("333.333.333-33,2,Carlos Renato da Silva, Jr.\r\n");

        var registro = Assert.Single(resultado.Registros);
        Assert.Equal("333.333.333-33", registro.Cpf);
        Assert.Equal("2", registro.IdResponsavel);
        Assert.Equal("Carlos Renato da Silva, Jr.", registro.NomeResponsavel);
        Assert.Equal("333.333.333-33,2,Carlos Renato da Silva, Jr.", registro.TextoBruto);
    }

    [Fact]
    public void Ignora_linhas_vazias_e_numera_somente_as_nao_vazias()
    {
        var resultado = ParserTxt.Interpretar("\n22222222222,1,Maria\n   \n\r\n33333333333,2,Carlos\n");

        Assert.Equal([0, 1], resultado.Registros.Select(r => r.Indice));
    }

    [Fact]
    public void Linha_com_menos_de_tres_campos_eh_invalida()
    {
        var registro = Assert.Single(ParserTxt.Interpretar("22222222222,1").Registros);

        Assert.False(registro.EstruturaValida);
    }
}

/// <summary>T031 — validação, duplicidade e chave de idempotência (FR-013, data-model §1).</summary>
public sealed class ValidadorRegistroTests
{
    private static IReadOnlyList<RegistroEntrada> Validar(string txt, string hash = "raiz") =>
        ValidadorRegistro.Validar(ParserTxt.Interpretar(txt).Registros, Instancia.Inter, hash);

    [Fact]
    public void Cabecalho_eh_dado_invalido_e_nao_eh_removido_silenciosamente()
    {
        var registros = Validar("cpf,id_responsavel,nome_responsavel\n22222222222,1,Maria");

        Assert.Equal(ValidacaoRegistro.DadoInvalido, registros[0].Validacao);
        Assert.Equal(ValidacaoRegistro.Valido, registros[1].Validacao);
    }

    [Fact]
    public void Cpf_repetido_vira_duplicado_a_partir_da_segunda_ocorrencia()
    {
        var registros = Validar("22222222222,1,Maria\n222.222.222-22,2,Outro");

        Assert.Equal(ValidacaoRegistro.Valido, registros[0].Validacao);
        Assert.Equal(ValidacaoRegistro.Duplicado, registros[1].Validacao);
        Assert.Null(registros[1].ChaveIdempotencia);
    }

    [Theory]
    [InlineData("22222222222, ,Maria")]
    [InlineData("22222222222,1,  ")]
    public void Responsavel_vazio_eh_dado_invalido(string linha) =>
        Assert.Equal(ValidacaoRegistro.DadoInvalido, Validar(linha)[0].Validacao);

    [Fact]
    public void Espacos_nas_pontas_dos_campos_sao_removidos()
    {
        var registro = Validar("  22222222222 ,  1 ,  Maria Eduarda  ")[0];

        Assert.Equal(("22222222222", "1", "Maria Eduarda"), (registro.Cpf, registro.IdResponsavel, registro.NomeResponsavel));
    }

    [Fact]
    public void Chave_eh_deterministica_e_depende_de_instancia_raiz_e_cpf()
    {
        var a = Validar("22222222222,1,Maria", "h1")[0].ChaveIdempotencia;
        var b = Validar("222.222.222-22,9,Outro nome", "h1")[0].ChaveIdempotencia;
        var c = Validar("22222222222,1,Maria", "h2")[0].ChaveIdempotencia;
        var d = ValidadorRegistro.Validar(ParserTxt.Interpretar("22222222222,1,Maria").Registros, Instancia.Condominio, "h1")[0].ChaveIdempotencia;

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(ValidadorRegistro.Chave(Instancia.Inter, "h1", "22222222222"), a);
    }

    [Fact]
    public void Registro_invalido_recebe_chave_por_indice()
    {
        var registro = Validar("xx,1,Maria", "h1")[0];

        Assert.Equal(ValidadorRegistro.ChaveInvalido(Instancia.Inter, "h1", 0), registro.ChaveIdempotencia);
    }
}
