namespace Dante.Application.IntegracaoDeCi;

public enum TipoDeEvidenciaDeCi
{
    // Etapa que o provedor marcou como falha, com job e número.
    EtapaComFalha,
    // Trecho sanitizado do log do job.
    TrechoDeLog,
    // Mensagem estruturada do próprio provedor (anotação, motivo de cancelamento, aviso de cobrança).
    AnotacaoDoProvedor,
    // Job sem runner além do limite da política, medido pelos horários do provedor.
    TempoSemRunner,
    // Resposta do provedor a uma consulta (log expirado, negado, ausente), sem corpo nem URL de API.
    RespostaDoProvedor
}
