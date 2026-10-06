namespace Dante.Application.QualidadeDoBrain;

public sealed record AchadoDeQualidadeDto(ProblemaDeQualidade Problema, Guid IdConhecimento, Guid? IdRelacionado, string Motivo);
