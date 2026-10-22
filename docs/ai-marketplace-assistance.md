# AI assistance for sellers, customers and wards

AIP-01..04 help vendors and customers; AIB-01 helps the ward officer who reads a citizen report.
Under BR-41 every one of them only **suggests**: nothing here saves, orders, publishes, accepts,
rejects or closes anything, and every screen still works with the model off.

## Endpoints

| Feature | Method and route | Who |
| --- | --- | --- |
| AIP-01 dish description | `POST /api/ai/dish-description` `{ name, imageUrl? }` | signed-in vendor |
| AIP-02 dish recognition | `POST /api/ai/dish-recognition` `{ imageUrl }` | signed-in vendor |
| AIP-03 natural-language search | `POST /api/ai/search-intent` `{ text }` | anyone, including guests |
| AIP-04 review summary | `GET /api/ai/storefronts/{storefrontId}/review-summary` | anyone, public storefronts only |
| AIB-01 list reports | `GET /api/ward/vendor-reports?status&page` | ward officer, own ward |
| AIB-01 one report | `GET /api/ward/vendor-reports/{reportId}` | ward officer, own ward |
| AIB-01 analyse again | `POST /api/ward/vendor-reports/{reportId}/assist` | ward officer, own ward |

A report outside the officer's ward is answered "not found", the same as one that does not exist.
`GET /api/uploads/evidence/{owner}/{file}` now also lets the ward officer of the report open the
photo attached to it (and a customer may upload that photo through `POST /api/uploads/evidence`).
The photo is only ever read by the owner and that officer.

`available: false` (or `aiAvailable: false`) is a normal answer, not an error: the feature is
off, the model did not answer, or there is not enough to go on. The screens then say so and carry on.

## What each one does

- **AIP-01** drafts a description (at most 400 characters) from the dish name and, if given, the
  vendor's own photo. The text only fills the form after the vendor presses "Dùng mô tả này".
- **AIP-02** suggests a dish name, one of the platform's own categories (ADM-01) and a confidence.
  The category id must be one that was offered, otherwise it is dropped. The reference price
  (lowest, median, highest) is computed from dishes currently on sale in that category, never
  by the model, and only when at least 3 dishes exist.
- **AIP-03** turns a sentence into the marketplace's own filters (category, price cap, open
  now, minimum rating, near me with a radius, sort). Values out of range are dropped, not bent;
  a reversed price range is discarded; `distance` sort needs "near me". When the model is off,
  or fails, a built-in Vietnamese keyword reader answers instead and the reply says
  `source: "RULES"`. The same sentence is answered from the AI log instead of paying twice.
- **AIP-04** summarises the written reviews of one public storefront in 2-3 sentences with up to
  three praised and three criticised points. It is offered only from `ReviewSummaryRules.MinReviews`
  (5) written reviews, reviews hidden by a moderator are left out, and the summary is cached until a
  review is added, removed or edited.
- **AIB-01** puts two kinds of note beside a citizen report:
  - computed by the system, no model: whether the scanned permit is valid now (permit and
    contract both active), whether it belongs to the slot the report names, and flags
    `NO_PHOTO`, `PERMIT_NOT_VALID`, `PERMIT_SLOT_MISMATCH`;
  - read from the photo by the model: signage text, the place recognised, whether a permit or QR is
    visible (`YES`/`NO`/`UNCLEAR`), a short description, and the flags
    `NO_PERMIT_VISIBLE_IN_PHOTO` and `SIGNAGE_DIFFERS_FROM_REGISTERED_NAME` (no word of 3+ letters in
    common with the registered business name, accents ignored).
  The place read from the photo is stored in `VendorReports.ai_extracted_location` (200 characters).
  When a citizen files a report, the photo is read once in the background
  (`VendorReportPreprocessor`, a bounded queue that drops work rather than blocking the request and
  never throws); the officer can also ask for a fresh reading.

## Keeping the model on a short lead

- **Inputs are data.** Dish names, search sentences and reviews are placed in the prompt as quoted
  data with angle brackets replaced, and the prompt tells the model never to follow instructions found
  inside them. Reviews are wrapped in `<review>` tags that user text cannot close.
- **Outputs are untrusted.** The reply must be a JSON object; each field is validated against its
  own schema (types, ranges, lists offered), then stripped of markup, links, e-mail addresses and
  Vietnamese phone numbers and capped in length. Unknown fields are ignored: a reply that says
  `"action": "close_report"` changes nothing, because the assistant has no way to change a report
  (the only write is the location note).
- **Images are our own uploads.** The server reads a photo from storage by its upload address
  (the vendor's own menu image, or the report's evidence image); any other address is refused and
  never fetched, so the feature cannot be pointed at an internal URL.
- **Failure is quiet.** `GeminiSuggestionClient` never throws; the shared client already retries
  across keys and models with per-attempt timeouts.
- **Everything fresh is logged** in `AIAssistanceLogs` (feature codes `AIP-01`..`AIP-04`, `AIB-01`;
  entity types `Vendor`, `Storefront`, `Marketplace`, `VendorReport`) with the input key used as
  the cache key. A cache hit is not logged again. No tokens, no phone numbers.
- **Rate limit.** Policy `AiAssist`: 20 calls a minute per signed-in user, 8 a minute per address
  for guests; over the limit is HTTP 429.

## Configuration

| Setting | Meaning |
| --- | --- |
| `Ai:Enabled` | Master switch; on when absent. |
| `Ai:Features:AIP-01` ... `Ai:Features:AIB-01` | Switch one feature off with `false`. |
| `AiCompliance:Gemini:ApiKey` / `ApiKeys` | The model key(s). User-secrets or environment variables only (`AiCompliance__Gemini__ApiKey`), never in a committed file. |
| `AiCompliance:Gemini:Model`, `FallbackModels`, `AttemptTimeoutSeconds` | Model choice and timeouts; the defaults are in `GeminiVisionClient`. |

With no key configured every feature behaves as "model unavailable": AIP-03 falls back to the
keyword reader, the others say no suggestion is available, and AIB-01 still shows the permit check.

## Thresholds that need a decision from the owner of the rule

These are working defaults, not agreed policy: minimum 5 reviews for AIP-04; at least 3 dishes for a
reference price (AIP-02); a 3-letter word as the unit of "same name" for the signage flag (AIB-01);
the AI rate limits above. Change them in `ReviewSummaryRules`, `AiMarketplaceRepository`,
`VendorReportAssistRules` and `AiRateLimitSetup`.

## Not covered

- Closing or dismissing a citizen report from the ward screen: the backend has no endpoint for it,
  so the live screen is read-only and links to recording a violation.
- The model's quality has not been evaluated against real photos or reviews; tests use a scripted
  fake model, so they prove validation, fallback and safety rules, not accuracy.

## Verification

```powershell
dotnet test StreetBiz.Backend.sln --no-build
```

Backend tests: `AiMarketplaceTests` (prompt-injection, schema, fallback, cache, ward scoping),
`AiMarketplaceRepositoryTests`, `AiFeatureGateTests`, `UploadsControllerTests`, and the endpoint
lists in `ControllerEndpointContractTests`.
