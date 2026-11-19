# PITACO 2.4 — Alinhamento temporal monotônico (Unity)

**Escopo:** experimento isolado na branch `test/unity-ppg-monotonic-clock-alignment`, sobre `test/unity-ppg-critical-hardening` (PR #11).  
**Estado:** implementado e com testes Edit Mode escritos. **Ainda nao compilado/testado no Unity ou no hardware neste atendimento.**  
**Firmware `PHV00/PITACO_ESP32`: preservado, nenhuma alteracao.**

## Problema observado

No teste anterior a Unity sofreu `UNITY_FRAME_STALL gapS=5.18` e a serial acumulou ate 233 linhas, com espera maxima `5109ms`. Enquanto o worker USB continuava lendo, a main thread processava dados antigos. A PR #11 passou a considerar a idade **na fila**, mas ainda faltava estimar o deslocamento de `millis()` do ESP32 para o relogio do host, distinguir restarts e medir atrasos antes do enqueue.

**Limitacao do firmware:** `sample.t_ms` e o momento de leitura da FIFO pelo firmware, **nao o instante exato da conversao ADC**. A conexao e USB serial (uma so COM) e as amostras sao produzidas a aproximadamente 25Hz.

## Fluxo implementado

```text
ESP32-C3/MAX3010x: {"type":"sample","seq":N,"t_ms":D,...}
      |
      v
USB worker: SerialPort.ReadLine() -> Stopwatch.GetTimestamp()  <--- ANTES da fila
      |
      v
PitacoConnectionManager.Packet { Text, EnqueuedTick, ArrivalMonotonicTicks }
      | fila em ordem, mesmo limite/budget anterior
      v
PitacoConnectionManager.Update(): CurrentDispatchArrivalTicks temporario
      |
      v
SerialControllerPitaco24.ManagedLine() -> OnJsonReceived()
      |
      v
PpgProcessingCoordinator:
   - aligner.Observe(seq, t_ms, arrivalStopwatchMs)
   - 3 amostras antes de confiar no mapeamento
   - estimativa de offset = min observado (atualizado lentamente para drift)
   - rollover seq uint16 e millis uint32 reconhecidos
   - regressao nao-rollover -> CLOCK_EPOCH_RESET + ResetMeasurements/G1
   - gap >750ms entre tempos de amostras -> CLOCK_SAMPLE_GAP (diagnostico)
   - BPM/G1/contato, janela 8s e thresholds INALTERADOS
      |
      v
OnMeasurement() -> age no instante da publicacao
      |
      v
OximetryDataBridge:
  effectiveAge = elapsedStopwatchSincePublication
               + max(queueAgeAtPublish, estimatedDeviceAgeAtPublish)
  se offset ainda indisponivel, fallback para idade da fila (PR #11).
  se effectiveAge >2s -> sem snapshot fisiologico
      |
      v
Mixer / HUD: nao transformar medidas antigas em LIVE
```

## Metodologia e garantias limitadas

`PpgMonotonicClockAligner` usa `System.Diagnostics.Stopwatch` e e independente de Unity. Calcula:

```text
offset_observado_i = chegada_worker_host_ms_i - t_ms_dispositivo_desembrulhado_i
offset_estimado ~= limite inferior de offsets observados (com slewing max. 100ppm)
idade_estimada_na_chegada_i = max(0, chegada_i - (device_ms_i + offset_estimado))
idade_no_mixer = tempo_monotonico_desde_publicacao
              + max(atraso_fila, idade_estimada_ao_publicar)
```

A diferenca entre o instante de chegada e o tempo de firmware pode conter latencia USB, bloqueio do firmware ou tempo de esvaziamento de buffers. **Nao e sincronizacao exata NTP, nao isola latencia em um unico sentido, nem mede diretamente o momento do pulso fisiologico.** A estimativa e um limite conservador no contexto das observacoes; sempre mantem o limite inferior da fila de Unity. Se nao houver 3 amostras temporais boas, usa somente atraso da fila. O estimador **nao faz interpolacao de PPG**, nao altera qualidade ou contato.

`CLOCK_EPOCH_RESET` significa **suspeita** de novo ciclo de tempo/seq; pode ser reboot ou anomalia de stream. Nao se afirma que o dispositivo reiniciou fisicamente sem mensagem de boot. Uma redefinicao da epoch invalida deliberadamente G1/buffer para nao herdar uma aprovacao antiga.

As contagens `clockRestarts`, `clockWraps` e `clockSampleGaps` sao diagnosticas. Os offsets podem ser negativos e grandes por terem origens arbitrarias. **Nao interpretar o valor de `clockOffsetMs` como latencia ou erro temporal absoluto.** Usar `ingressExcessMs`, `publishQueueLagMs` e `alignedPublishAgeMs`.

## Testes Edit Mode: executar depois

1. Fazer checkout da branch e abrir o projeto.
2. Esperar compilacao, verificar Console sem erros.
3. Abrir **Window > General > Test Runner > EditMode**.
4. Rodar `PpgMonotonicClockAlignmentTests` (**12 testes**):
   - warmup de 3 frames / estimativa base;
   - atraso USB de 140ms detectado como excesso;
   - stall Unity de 5s nao renova amostra;
   - regressao de `millis()` e sequencia com reinicio de epoch;
   - rollover de `uint32 millis()` e `uint16 seq` sem falso reboot;
   - gap de FIFO >750ms diagnosticado;
   - ordem regressiva de seq resetada conservadoramente;
   - tempo host invalido nao cria sincronismo;
   - idade alinhada nunca menor que atraso conhecido da fila;
   - amostra >2s inelegivel no Bridge, fresca segue elegivel para BPM;
   - Clear remove mapeamento.
5. Rodar tambem `OximetryDataBridgeTests` (8 testes) e o menu `Tools > PITACO > Run PPG critical hardening smoke tests` da PR #11. Os asserts antigos que pressupunham SpO2 DEMO ativado por padrao foram corrigidos **apenas no arquivo de teste**.

## Testes Play Mode com hardware: executar depois

**A. Sessao normal / dedo fixo:**
- COM3/115200 (ou a COM efetivamente detectada). Deixar opt-in SpO2 DEMO em `false`.
- Esperar 5-10s e confirmar `[PPG-DIAG] event=SUMMARY ... clockReady=True`.
- Com contato e G1 PASS, BPM pode aparecer apos a janela de ~8s, condicionado a SQI/autocorrelacao.
- Confirmar `clockRestarts=0` e `clockSampleGaps=0` em aquisicao continua; `clockOffsetMs` aproximadamente estavel; `ingressExcessMs` geralmente baixo, mas variavel com USB.
- `G1_REJECT` e `BPM_REJECT_STREAK` continuam com mesmas condicoes de antes; nenhum bypass.

**B. Congelamento/scene load:**
- Abrir cena `Plataform` enquanto USB envia.
- Registrar `UNITY_FRAME_STALL`, `[PITACO-QUEUE]`, `BRIDGE_TRANSITION` e `SUMMARY`.
- Quando `alignedAgeAtPublishMs` ou `publishQueueLagMs` superar 2000, o Bridge deve indicar `PUBLISHED_MEASUREMENT_STALE` (ou transicao apropriada por G1/contato) e negar dados ao Mixer.
- Quando chegam leituras atuais e G1/contato validos, `AVAILABLE` pode voltar apos 8s de janela de processamento, se a autocorrelacao aprovar. Nao se deve assumir que cada stall interrompa o firmware.

**C. Falha do sensor / fluxo PPG ausente:**
- Verificar eventos `X,PPG_STATE`, `X,PPG_ERROR`, `X,PPG_FIFO_*` pelo monitor serial de bancada em ensaio separado, sem abrir simultaneamente a COM na Unity.
- Apos 3s sem JSON PPG, `TELEMETRY_LOST` continua bloqueando HUD/Mixer; sincronizacao de relogio nao resolve perda real de aquisicao.
- Se houver regressao de seq/t_ms ao retomar, verificar `CLOCK_EPOCH_RESET` e nova aprovacao G1. Caso contrario, `CLOCK_SAMPLE_GAP` para lacuna observavel.
- Desconexao fisica da COM deve reinicializar alinhamento na reconexao.

**D. Regressao do comportamento existente:**
- Confirmar protecao da PR #11 contra SpO2 DEMO (regras de paciente inelegiveis).
- Nenhuma mudanca em `PpgSampleBuffer`, `PpgSignalProcessor`, `BpmEstimator`, `Spo2Estimator`, `PITACO_ESP32` ou observacoes ONNX.
- Se o tempo de entrada nas regras aumenta indevidamente para dados que sao bons, investigar `ingressExcessMs` e clocks; **nao** relaxar os 2s sem registrar evidencias.

## Limites do teste

Este commit nao e uma prova fisica de sincronizacao de clock: os tests Edit Mode injetam `arrivalHostMs` simulados e avaliam a logica deterministica, nao ligam COM nem testam que o ESP32 reporta `t_ms` confiavelmente. A coerencia com o dispositivo, a causa do stall 5s e falhas FIFO/I2C exigem novo log e teste no hardware. **Nao fazer merge antes de testar no Unity.**
