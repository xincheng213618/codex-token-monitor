# Bundled real-price reference data

Source: [Real API Pricing](https://github.com/FeiZhuLulu/real-api-pricing), snapshot 2026-09-27,
commit `c79088522db653986becfc42a892a7dafa62c6eb`.

CodexTokenMonitor extracts 53 OpenAI / Zhipu subscription-and-model records from
`web/public/data/site.json`, retains their evidence and adoption notes, and embeds
them for offline use. Zhipu prices are calculated in CNY from original monthly
fees and token allowances. The application selects each model's highest Zhipu
unit price to estimate the reference value of free ZCode usage; this is not an
actual payment or a claim that ZCode shares those paid-plan allowances.

The upstream MIT license is in `LICENSE.txt`; third-party attribution and source
terms are retained in `SOURCES.md`. The selected dataset includes derived
[awesome-coding-plan](https://github.com/mahonzhan/awesome-coding-plan) measurements
by mahonzhan@gmail.com under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
The extraction, selection and CNY calculation above are CodexTokenMonitor changes;
the source authors do not endorse this application or its estimates.

## GPT-6.1 Sol supplements (2026-10-02)

The original 53-record snapshot remains unchanged. Two GPT-6.1 Sol records are
added with their own snapshot dates and evidence:

- Plus: $20 / 870M monthly tokens = $0.022988506/MTok, from upstream
  `data/adopted.csv` at commit `35b06e01ea25dd0ee0d34cf43e4654042c286f84`.
  The [three-segment measurement](https://github.com/FeiZhuLulu/real-api-pricing/blob/35b06e01ea25dd0ee0d34cf43e4654042c286f84/data/research/chatgpt-gpt61sol-plus-round3-2026-09-30.json)
  was adopted on 2026-10-01 using the upstream standard workload conversion.
- Pro 20x: CodexTokenMonitor's own local quota measurement, distinct from the
  upstream dataset. See [calculation and limitations](LOCAL-MEASUREMENTS.md).

No Pro 5x record is inferred. Existing same-model fallback rules continue to
select the highest reference price and label a different plan when applicable.
