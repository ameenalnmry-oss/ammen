# Water Profile Source Mapping — PharmaLIMS 2026.9.6.236

## Source status

This mapping is an implementation traceability aid. It does not replace the controlled
documents. The operator/reviewer must confirm that the site's controlled copies are
current before Review/Approval.

Primary site source supplied for this change:
- MQC-G-0018, WATER ANALYSIS, Version 2.0, 19 pages.

Optional older corporate source supplied for PTW supplement:
- CTG-11-02, WATER FOR PHARMACEUTICAL USE, Version 02, issue date 14-Feb-2014,
  Annexure CTG 11/A3.

## MQC-G-0018 v2.0 — auto-selected criteria

| Test | PW | PTW | Controlled criterion represented in profile | Source |
|---|---|---|---|---|
| pH | Yes | Yes | PW 5.0–7.0; PTW 6.5–8.5 | §2.13.2.1 |
| Conductivity | Yes | Yes | PW ≤1.3 µS/cm; PTW ≤500 µS/cm | §2.13.1.5 |
| TDS | Yes | Yes | PW ≤1 mg/L; PTW ≤500 mg/L | §2.13.1.5 |
| TOC | Yes | No | PW ≤500 ppb; Alert 300 ppb; Action 500 ppb; PTW N/A | §2.13.3.4–5 |
| TAMC | Yes | Yes | PW NMT 100 CFU/mL; PTW NMT 500 CFU/mL | §2.7 |
| Escherichia coli | Yes | Yes | Absent per 100 mL / confirm suspect growth | §§2.4.4, 2.7 |
| Salmonella | Yes | Yes | Absent per 100 mL / confirm suspect growth | §§2.4.5, 2.7 |
| Pseudomonas aeruginosa | Yes | Yes | Absent per 100 mL / confirm suspect growth | §§2.4.6, 2.7 |
| Staphylococcus aureus | Yes | Yes | Absent per 100 mL / confirm suspect growth | §§2.4.7, 2.7 |
| Burkholderia cepacia complex | Conditional | Conditional | Absent when applicable under MQC-G-0034/risk evaluation | §2.4.8 |
| BET | Conditional | No | PW as required; <0.25 EU/IU per mL under applicable compendium | §2.8 |
| Hardness | No fixed PW criterion | Yes | PTW <300 ppm as CaCO3 | §2.14.10.2 |
| Acidity | Yes | Yes | Resulting solution is not red | §2.14.12 |
| Ammonium | Yes | Yes | Test solution not more intensely coloured than comparator | §2.14.14 |
| Heavy Metals | Yes | Yes | Test-solution colour not more intense than specified Pb standard | §2.14.15 |
| Chlorides | Yes | Yes | Appearance does not change for at least 15 min | §2.14.16 |
| Nitrates | Yes | Yes | Blue colour not more intense than specified nitrate comparator | §2.14.17 |
| Sulphates | Yes | Yes | Appearance does not change for at least 1 h | §2.14.18 |
| Oxidisable Substances | Yes | Yes | Solution remains faintly pink after specified procedure | §2.14.19 |
| Residue on Evaporation | Yes | Yes | NMT 1 mg residue from 100 mL sample (0.001%) | §2.14.20 |

TAMC Alert/Action trend limits are intentionally not auto-populated. MQC-G-0018
states they are established under MQC-G-0011.

## MQC-G-0018 method-only / no universal criterion auto-selected

The test catalog contains these rows so they can be configured when a controlled
site report/specification supplies the missing acceptance criterion, but the source
helper does not automatically put them into an Approved operational profile:

- Appearance / Odor: §2.14.3 describes physical examination but does not state a
  universal acceptance criterion in the supplied SOP text.
- Total Suspended Solids: §2.14.11 provides a method/formula but no general PW/PTW
  acceptance limit.
- Alkalinity: §2.14.13 gives an interpretation of blue colour but no universal
  accept/reject criterion.
- Residual / Free Chlorine: §§2.14.21–2.14.21.4 provide method/calculation and say
  to compare against the standard required for the application; no single PW/PTW
  acceptance interval is stated.
- PW Hardness: the table gives Pre-RO/Post-RO/EDI/WFI/PTW/boiler values but no
  general final PW hardness limit.
- PTW TOC: explicitly N/A in §2.13.3.4.
- PTW BET: not stated as a PTW requirement in §2.8.

## Optional CTG-11-02 v02 — PTW supplement only

The application exposes this as a separate Development Admin authoring action; it is
not merged automatically with MQC-G-0018.

| Test | Maximum / criterion transcribed from Annexure CTG 11/A3 |
|---|---|
| Arsenic | 0.05 ppm |
| Barium | 1 ppm |
| Cadmium | 0.010 ppm |
| Chromium | 0.05 ppm |
| Lead | 0.05 ppm |
| Selenium | 0.01 ppm |
| Mercury | 0.002 ppm |
| Fluoride | 1.4 ppm |
| Nitrates (as N) | 10 ppm |
| Nitrites (as N) | 1 ppm |
| Turbidity | 1 NTU |
| Endrin | 0.0002 ppm |
| 2,4 DDT | 0.0002 ppm |
| 4,4 DDT | 0.0002 ppm |
| Gross Alpha + Gross Beta | total 10 pCi/L |
| Ra-226 + Ra-228 | total 5 pCi/L |
| Total Coliforms | Less than 2 (source table does not state separate unit/sample basis) |

Because CTG-11-02 is an older corporate guideline, use this supplement only after
site Quality confirms it is still an adopted/current controlled requirement.

## Data representation

- Numeric source limits remain numeric in WaterSpecifications.
- Qualitative chemical criteria use controlled text in SpecificationText and
  `Complies` / `Does Not Comply` at results entry.
- The existing numeric SampleTests.ResultValue contract is preserved with
  0 = Complies/Absent and 1 = Does Not Comply/Present for qualitative tests.
- SpecificationText and numeric limits are frozen into SampleTests at direct Water
  registration.
