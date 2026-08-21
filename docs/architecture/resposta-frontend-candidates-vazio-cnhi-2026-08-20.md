# Resposta ao handoff do front-end — `candidates: []` CNHI (2026-08-20)

Confirmado: **mesmo layout já investigado em 2026-08-12** (issues #38/#39/#40), mas
**diferente do padrão de causa do dia de hoje** (config `AllowedPackageGuids`/`RunnerPath`
ausente, #107/#108). As 3 causas originais (exceção de SQL engolida, convenção errada do
pathway tcl-xsl, IA nunca chamada em `execute-candidates`) **já têm fix aplicado e confirmado
presente no código atual**. Catálogo e mapper foram confirmados corretos/publicados por SQL do
dono na época — não é "mapper não existe".

Se o sintoma `candidates: []` ainda aparece na build/deploy que vocês estão testando, não é
regressão das 3 causas antigas — precisamos de um `CorrelationId`/log estruturado da chamada
real que falhou pra identificar a causa nova. Sem isso não há ação de código a propor.

Detalhe completo: `docs/architecture/diagnostico-candidates-vazio-cnhi-2026-08-20.md`.
