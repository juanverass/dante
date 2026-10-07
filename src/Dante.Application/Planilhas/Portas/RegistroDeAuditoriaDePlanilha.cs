namespace Dante.Application.Planilhas;

// Uma célula alterada: conta lógica (sem token), planilha, aba, operação, endereço, valores e origem do pedido.
public sealed record RegistroDeAuditoriaDePlanilha(
    DateTimeOffset Instante,
    string? Conta,
    string IdDaPlanilha,
    string Alias,
    string Aba,
    string Operacao,
    string Endereco,
    string? ValorAnterior,
    string? FormulaAnterior,
    string ValorNovo,
    string Origem,
    string? Agente);
