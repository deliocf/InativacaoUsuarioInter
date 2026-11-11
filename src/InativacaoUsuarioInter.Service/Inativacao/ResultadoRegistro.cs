namespace InativacaoUsuarioInter.Service.Inativacao;

/// <summary>Resultado do processamento de um registro (data-model §1).</summary>
public enum ResultadoRegistro
{
    Inativado,
    JaInativo,
    CpfNaoEncontrado,
    DadoInvalido,
    PendenteTransitorio,
}

/// <summary>Estado do usuário no WAccess antes da alteração (research R-03, passo A).</summary>
public enum EstadoAnterior
{
    Ativo,
    Inativo,
    Inexistente,
}
