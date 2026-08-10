---
name: gabarito-fiat-comando-de-verificacao
description: O comando exato da equivalência byte a byte contra .claude/tmp/exemplos/ — o mapper é MAP_MQSERIES_SEND_ENV_TXT_XML_NFE, e há um MAP_MARELLI_ homônimo que produz saída errada com exit=0.
metadata:
  type: project
---

Comando canônico do gate de equivalência do runner low-code (rodar **de dentro da Bin da
instância**, ver [[runner-lowcode-roda-da-bin-nao-de-functions]]):

```
LayoutParserLowCodeRunner.exe
  --globalFolder <qualquer um dos dois globalfolder>
  --package    938f9978-836f-48c1-9c0f-c2898caf4b20
  --mapperId   MAP_f31a6758-69c9-4cf6-92d2-24f0e27a1ab5
  --inputFile  ".claude/tmp/exemplos/txt input/QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt"
  --outputFile <saida>
  --fileName   QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt
```

Esperado: `exit=0`, **4246 bytes**, idêntico ao gabarito
(`.claude/tmp/exemplos/xml output/...-11072026094950273-env.xml`, 4245 bytes) tolerando **só** o
espaço duplo em `<?xml  version=`.

**Why:** `MAP_f31a6758` chama-se `MAP_MQSERIES_SEND_ENV_TXT_XML_NFE`. Existe no mesmo catálogo um
`MAP_MARELLI_MQSERIES_SEND_ENV_TXT_XML_NFE` (`MAP_1cfab556-4b0e-45ce-baee-4f9570f1ca51`) cujo nome
convida ao erro: ele roda, sai com **exit=0** e produz 2852 bytes — faltando `<total>`/`<ICMSTot>`,
`<transp>`, `<cobr>`, `<pag>`, `<compra>` e **sobrando** `<B2B>`, `<comb>`, `<descANP>`. Custou um
ciclo da arquiteta. Sinal de diagnóstico: elementos **a mais** indicam mapper errado, não regressão —
uma regra que falha remove nós, não inventa.

**How to apply:** o `globalFolder` **não** é a variável: os dois catálogos existentes nesta máquina
(`tools/LowCodeRunner/globalfolder` → `export.context/exportContext.data`, 48.636.594 B; e
`C:\inetpub\wwwroot\layoutparser\globalfolder` → 124.351.026 B) têm tamanhos bem diferentes mas
listam **170 mapeadores cada** e **ambos** contêm os dois mappers acima. Verificado 2026-08-10: o
gate passa idêntico com qualquer um dos dois. Antes de suspeitar de regressão do runner, rode o
modo `LIST` e confira o GUID.
