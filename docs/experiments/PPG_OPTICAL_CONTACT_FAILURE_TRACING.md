# PPG 2.4 — Investigacao de contato optico e reinicio do G1 (Unity)

**Escopo:** instrumentacao da branch `test/unity-ppg-monotonic-clock-alignment` (PR #12). O firmware em `PHV00/PITACO_ESP32` permanece FROZEN. Nenhum limiar, gate, estimador, frequencia ou regra de publicacao foi alterado.

## Motivo

No ensaio anterior, a Unity registrou `CONTACT_GLITCH` e `G1_REVOKED` por volta de 34 s e `CONTACT_LOST` perto de 95 s, embora o dedo estivesse posicionado. O objetivo do commit nao e mascarar `--`, mas determinar se houve limiar optico, debounce, comando de ciclo de vida ou erro de FIFO/I2C.

## Novos eventos esperados no Console / Editor.log

| Evento | Origem | Significado |
| --- | --- | --- |
| `[PPG-DIAG] event=OPTICAL_EDGE transition=TO_INVALID` | Unity | Primeira amostra com `sqi.contact=false` de uma sequencia; inclui ate 6 amostras anteriores |
| `[PPG-DIAG] event=OPTICAL_SAMPLE phase=INVALID` | Unity | Primeiras 3 amostras recusadas e, se persistir, 1 a cada 125 |
| `[PPG-DIAG] event=OPTICAL_EDGE transition=TO_VALID` | Unity | Primeira amostra novamente aceita, com duracao da sequencia invalida em numero de amostras |
| `[PPG-DIAG] event=OPTICAL_SAMPLE phase=RECOVERY` | Unity | Ate 4 amostras apos a recuperacao |
| `[PITACO-CMD] phase=QUEUED` | Unity | Um componente solicitou um comando ao gerenciador (ainda nao confirma envio) |
| `[PITACO-CMD] phase=SENT` | Worker USB, despacho Unity | O worker executou `SerialPort.Write` |
| `[PITACO-CMD] phase=IDENTIFY_SENT/SHUTDOWN_SENT` | Worker USB, despacho Unity | Identificacao da COM ou parada no encerramento de sessao |
| `[PITACO-FW] X,PPG_*` / `X,MAX30105_*` | Firmware via USB | `PPG_STATE`, `PPG_CONTACT`, `PPG_FIFO_*`, `PPG_I2C_*`, `PPG_DIAG`, `PPG_ERROR`, etc. |

`OPTICAL_SAMPLE` registra `seq`, `devMs`, RED, IR, o flag de contato, o motivo do firmware e os indicadores **diagnosticos** `redBelow5000`, `irBelow5000`, `redNearClip` e `irNearClip`. Esses valores de referencia refletem o firmware atual e NAO sao uma nova validacao de contato. Um `fwContact=false` com RED/IR dentro dos limites pode significar espera de confirmacao de 3 amostras pelo firmware, nao defeito dos LEDs.

## Procedimento de reproducao

1. Reinicie a sessao de Play no Unity Editor, abra a cena do jogo e mantenha o dedo parado no sensor por pelo menos 3 minutos. Anote se o dedo ja estava no sensor ao iniciar.
2. Registre o horario aproximado de cada mudanca de BPM/SpO2 DEMO para `--` e de cada recuperacao. Nao mova o dedo intencionalmente durante o ensaio inicial.
3. Em um segundo ensaio, retire o dedo por 1–2 s e reposicione-o. Anote os dois eventos.
4. Salve o log **completo** do Console ou `Editor.log` (nao apenas os eventos de BPM). Filtro util: `[PPG-DIAG]`, `[PITACO-CMD]`, `[PITACO-FW]`, `[PITACO-QUEUE]`.
5. Para qualquer falha, compare a janela de aproximadamente 10 s antes/depois do `OPTICAL_EDGE`, os comandos `r/f/c/z` e os marcadores do firmware. Verifique se `X,PPG_STATE,STOPPED/COLLECTING` e `PPG_FIFO_*/I2C` coincidem com `G1_REVOKED`.

### Como interpretar

- RED ou IR abaixo de 5000: o firmware tem razao em declarar a amostra insuficiente; investigar contato optico, LED, alimentacao, fiação e ambiente.
- RED e IR normais **mas** `fwContact=false` apos uma transicao: investigar a histerese de confirmacao ou reinicio de sessao.
- `[PITACO-CMD] phase=SENT command=f/c/r` perto da falha e `X,PPG_STATE` correspondente: investigar a origem do comando no Unity.
- `X,PPG_FIFO_OVERFLOW`, `X,PPG_I2C_FIFO_READ_ERROR` ou `X,PPG_SW_RING_OVERRUN`: investigar aquisição e temporizacao no dispositivo; `serialDropped=0` na Unity nao exclui perda anterior ao envio USB.
- `AUTOCORR_BELOW_0_55` sem `WINDOW_RESET`: rejeicao isolada do estimador; analisar a janela de 8 s separadamente.

**Seguranca:** as medicoes SpO2 continuam sendo DEMO, sem calibracao clinica. Estes logs nao justificam suprimir as regras existentes de G1/contato nem reutilizar dados possivelmente invalidos.
