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
