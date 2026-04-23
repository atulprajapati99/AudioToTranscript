# Technical Document — Audio to Transcript (Call Center Voicemail Processing)

---

## Document Control

| Field | Value |
|---|---|
| Project | AudioToTranscript — Cisco UCCX Voicemail to Salesforce |
| Version | 1.0 |
| Author | Atul Prajapati |
| Date | April 2026 |
| Status | Draft |
| Repository | https://github.com/atulprajapati99/AudioToTranscript |

> **How to convert to Word:** Word 2019+ opens this file directly (File → Open → select `.md`).
> Or install Pandoc and run: `pandoc TechnicalDocument.md -o TechnicalDocument.docx`

---

## 1. Project Overview

### Business Purpose

Cisco Unified Contact Center Express (UCCX) receives customer voicemail recordings for a beverage distribution call center. When a customer calls and leaves a voicemail, the system must:

1. Receive the WAV recording and associated metadata from Cisco UCCX
2. Store the audio file securely in Azure Blob Storage
3. Transcribe the audio to text using Microsoft Azure Cognitive Services Speech API
4. Create a Salesforce Case with the transcription and caller metadata
5. Send email notifications to the operations team on success or failure
6. Maintain a 30-day auditable log of every request and its outcome

A secondary endpoint accepts image files and returns an immediate transcription result without any pipeline processing.

### Problem Statement

Cisco UCCX cannot natively integrate with Salesforce or perform speech-to-text transcription. This system acts as the middleware layer, receiving the voicemail from Cisco, processing it asynchronously, and pushing the result into Salesforce — all without requiring any change to Cisco's existing configuration beyond pointing to the new APIM endpoint.

### Out of Scope

- Any changes to Cisco UCCX configuration beyond setting the outbound webhook URL
- Custom Salesforce Case UI
- Real-time audio streaming (all processing is asynchronous)

---

## 2. System Architecture

### End-to-End Flow

```
┌─────────────────┐
│   Cisco UCCX    │  Customer leaves voicemail
│  (Call Center)  │
└────────┬────────┘
         │ POST multipart/form-data
         │ (WAV bytes + metadata JSON)
         ▼
┌─────────────────┐
│  Azure API      │  Validates Ocp-Apim-Subscription-Key
│  Management     │  Rate limit: 5 req/min
│  (APIM)         │  Enforces HTTPS / TLS 1.2
└────────┬────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│  Azure Functions App  (Consumption Plan — .NET 8 Isolated)  │
│                                                             │
│  ┌──────────────────────┐                                   │
│  │  ReceiveAudioFunction│  HTTP POST /api/audio             │
│  │  1. Parse multipart  │  → 202 Accepted immediately       │
│  │  2. Validate fields  │                                   │
│  │  3. Map call type    │                                   │
│  │  4. Upload WAV →     │──────────────────────┐            │
│  │     Blob Storage     │                      │            │
│  │  5. Write audit row  │────────────────┐     │            │
│  │  6. Enqueue message  │──────────┐     │     │            │
│  └──────────────────────┘          │     │     │            │
│                                    │     │     │            │
│  ┌──────────────────────┐          │     │     │            │
│  │  ReceiveImageFunction│  HTTP POST /api/image             │
│  │  Synchronous only    │  → 200 OK with {text, confidence} │
│  └──────────────────────┘          │     │     │            │
│                                    │     │     │            │
│  ┌──────────────────────┐          │     │     │            │
│  │  ReplayFunction      │  HTTP POST /api/replay            │
│  │  Re-enqueues failed  │──────────┘     │     │            │
│  │  blob without re-    │                │     │            │
│  │  uploading           │                │     │            │
│  └──────────────────────┘                │     │            │
└──────────────────────────────────────────┼─────┼────────────┘
                                           │     │
                          ┌────────────────┘     │
                          ▼                      ▼
              ┌───────────────────┐   ┌──────────────────────┐
              │  Azure Queue      │   │  Azure Blob Storage  │
              │  audio-processing │   │  Container: media    │
              │  -queue           │   │  media/YYYY-MM-DD/   │
              └────────┬──────────┘   │  {caseId}_{ts}.wav   │
                       │              └──────────────────────┘
                       │ Queue trigger fires
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  ProcessAudioFunction  (Queue Trigger)                       │
│                                                              │
│  Step 1: Download WAV from Blob Storage                      │
│  Step 2: POST to Azure Cognitive Services Speech API         │
│          → Retry up to 3× (2s / 4s / 8s backoff)            │
│          → On failure: email team + WAV attached → DLQ       │
│  Step 3: POST to Salesforce REST API (create Case)           │
│          → OAuth 2.0 token (cached, re-fetched on 401)       │
│          → Retry up to 3× (2s / 4s / 8s backoff)            │
│          → On failure: email team + transcript in body → DLQ │
│  Step 4: Send success email                                  │
│  Step 5: Update audit row → FinalStatus = Success            │
└──────────────────────────────────────────────────────────────┘
         │                        │                   │
         ▼                        ▼                   ▼
┌─────────────┐      ┌────────────────────┐  ┌──────────────┐
│  Azure      │      │  Azure Table       │  │  Email       │
│  Cognitive  │      │  Storage           │  │  (O365 SMTP) │
│  Services   │      │  AudioProcessingLog│  │              │
│  Speech API │      │  (30-day audit)    │  └──────────────┘
└─────────────┘      └────────────────────┘
         │
         ▼
┌──────────────────┐
│  Salesforce      │
│  REST API        │
│  POST /Case/     │
└──────────────────┘
```

### CleanupFunction (Timer Trigger — Daily 02:00 UTC)
Deletes audit rows from `AudioProcessingLog` where `RetainUntil < today`.
WAV blobs are cleaned up by Azure Blob Storage lifecycle policy (30-day rule).

---

## 3. Technology Stack

### Runtime

| Component | Technology |
|---|---|
| Language | C# (.NET 8) |
| Function host | Azure Functions v4 Isolated Worker |
| Process model | Two-process: func.exe host + dotnet worker |
| HTTP parsing | Microsoft.AspNetCore.WebUtilities |
| Email | MailKit 4.8.0 (SMTP / StartTLS) |
| Serialization | System.Text.Json (built-in) |

### Azure SDKs

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Azure.Functions.Worker | 2.0.0 | Core Functions runtime |
| Microsoft.Azure.Functions.Worker.Sdk | 1.17.4 | Build tooling, function discovery |
| Microsoft.Azure.Functions.Worker.Extensions.Http | 3.2.0 | HTTP triggers |
| Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues | 5.5.1 | Queue trigger + client |
| Microsoft.Azure.Functions.Worker.Extensions.Timer | 4.3.1 | Timer trigger (cleanup) |
| Microsoft.Azure.Functions.Worker.ApplicationInsights | 1.2.0 | Telemetry |
| Azure.Storage.Blobs | 12.22.0 | WAV file storage |
| Azure.Storage.Queues | 12.20.0 | Async processing queue |
| Azure.Data.Tables | 12.9.0 | Audit log table |
| Azure.Identity | 1.13.0 | Managed identity support |
| Azure.Security.KeyVault.Secrets | 4.7.0 | Secrets management |
| Microsoft.ApplicationInsights.WorkerService | 2.22.0 | App Insights integration |

### Testing

| Package | Version | Purpose |
|---|---|---|
| xUnit | 2.9.0 | Test framework |
| Moq | 4.20.72 | Mocking |
| FluentAssertions | 6.12.1 | Readable assertions |
| Microsoft.NET.Test.Sdk | 17.11.1 | Test runner |

---

## 4. Azure Infrastructure

### Services Required

| Service | SKU / Tier | Purpose | Est. Monthly Cost |
|---|---|---|---|
| **Resource Group** | Free | Logical container | $0 |
| **Azure Storage Account** | Standard LRS | Blob (WAV) + Queue + Table | ~$2–5 |
| **Azure Functions App** | Consumption (Y1) | Hosts all functions | ~$0 (1M free/mo) |
| **Log Analytics Workspace** | Pay-as-you-go | Backend for App Insights | ~$0 (5GB free) |
| **Azure Application Insights** | Pay-as-you-go | Logs, traces, alerts | ~$0 (5GB free) |
| **Azure Cognitive Services — Speech** | Standard S0 | Audio transcription | ~$1 per audio hour |
| **Azure API Management** | Developer / Standard | Auth, rate limiting | ~$50 / ~$300 |
| **Azure Key Vault** | Standard | Secret storage | ~$5 |

**Total (testing, no APIM/KV):** ~$2–10/month
**Total (production, all services):** ~$60–320/month depending on APIM tier

### Storage Account Sub-Resources

| Resource | Name | Type |
|---|---|---|
| Blob container | `media` | Private — WAV files |
| Queue | `audio-processing-queue` | Processing pipeline |
| Queue | `audio-processing-queue-poison` | Auto-created DLQ (after 5 failures) |
| Table | `AudioProcessingLog` | Audit trail (30-day retention) |

---

## 5. API Endpoints

### POST /api/audio — Submit Audio for Processing (Async)

**Request**
```
POST https://{apim-host}/api/audio
Ocp-Apim-Subscription-Key: {subscription-key}
Content-Type: multipart/form-data; boundary={boundary}

--{boundary}
Content-Disposition: form-data; name="metadata"
Content-Type: application/json

{
  "callType":  "D|FR",
  "caseId":    "0017V000001s0nwjQAA",
  "phone":     "9794920458",
  "timestamp": "17752345467",
  "brandId":   "4600"
}
--{boundary}
Content-Disposition: form-data; name="audio"; filename="recording.wav"
Content-Type: audio/wav

[raw WAV bytes]
--{boundary}--
```

**Metadata Fields**

| Field | Required | Description |
|---|---|---|
| `callType` | Yes | Cisco call type code (e.g. `D|FR`, `FeedBack|DriverFeedback`) |
| `caseId` | Yes | Salesforce Case ID (used as audit RowKey) |
| `phone` | Yes | Caller phone number |
| `timestamp` | No | Unix timestamp ms (used in blob path) |
| `brandId` | No | Brand identifier |

**Response — 202 Accepted**
```json
{ "id": "0017V000001s0nwjQAA", "status": "queued" }
```

**Response — 400 Bad Request**
```json
{ "error": "Metadata must include non-empty 'caseId' and 'phone'." }
```

---

### POST /api/image — Synchronous Image Transcription

**Request**
```
POST https://{apim-host}/api/image
Ocp-Apim-Subscription-Key: {subscription-key}
Content-Type: multipart/form-data; boundary={boundary}

--{boundary}
Content-Disposition: form-data; name="image"; filename="invoice.jpg"
Content-Type: image/jpeg

[raw image bytes]
--{boundary}--
```

**Response — 200 OK** (immediate — no queue)
```json
{ "text": "Invoice #12345 dated April 2026...", "confidence": 0.97 }
```

---

### POST /api/replay — Requeue a Failed Case

Used by operations team to retry a failed case without re-uploading the WAV.

**Request**
```
POST https://{apim-host}/api/replay
Ocp-Apim-Subscription-Key: {subscription-key}
Content-Type: application/json

{
  "blobPath":    "media/2026-04-16/0017V000001s0nwjQAA_1745000000000.wav",
  "caseId":      "0017V000001s0nwjQAA",
  "callTypeRaw": "D|FR",
  "phone":       "9794920458"
}
```

**Response — 202 Accepted**
```json
{ "id": "0017V000001s0nwjQAA", "status": "queued-for-replay", "blobPath": "media/..." }
```

---

## 6. Audio Pipeline — Detailed Flow

### Step 0: Receive (ReceiveAudioFunction)
1. Validate `Content-Type: multipart/form-data`
2. Parse multipart body → extract metadata JSON + WAV bytes
3. Validate `caseId` and `phone` are present
4. Map `callType` code → Salesforce fields via `CallTypeMapper`
5. Build blob path: `media/{YYYY-MM-DD}/{caseId}_{timestamp}.{ext}`
6. Upload WAV to Blob Storage (`BlobService.UploadMediaAsync`)
7. Write initial audit row: `FinalStatus = Received`
8. Serialize `ProcessingMessage` as JSON, enqueue to `audio-processing-queue`
9. Return `202 Accepted`

### Step 1: Transcription (ProcessAudioFunction — Queue Trigger)
- Download WAV from Blob Storage
- POST audio bytes to Speech API with headers:
  - `Ocp-Apim-Subscription-Key: {key}`
  - `Content-Type: audio/wav; codec=audio/pcm; samplerate=8000`
  - `Accept: application/json`
- Parse `{ "DisplayText": "...", "Confidence": 0.95 }` response
- Retry: up to 3 attempts, 2s/4s/8s backoff
- Retry on: timeout, 5xx errors
- No retry on: 400, 415 (bad request — won't fix itself)
- **Success:** update audit `TranscriptionStatus = Success`
- **All attempts fail:** update audit `TranscriptionStatus = Failed`, send failure email with WAV attached, rethrow → queue retries up to 5×, then DLQ

### Step 2: Salesforce Case (ProcessAudioFunction)
- POST to Salesforce REST API (`/services/data/v52.0/sobjects/Case/`)
- OAuth 2.0 client credentials flow (token cached in memory, refreshed on 401)
- Request body:
```json
{
  "Status":                "New",
  "CaseOrigin__c":         "IVR",
  "Phone":                 "{phone}",
  "BrandId__c":            "{brandId}",
  "CallType__c":           "{mapped call type}",
  "CSTproblemReported__c": "{mapped CST problem}",
  "Description":           "IVR notified caller of delivery date and delivery plan date. Here's what they said: {transcriptionText}",
  "RecordTypeId":          "{from config}",
  "OwnerId":               "{from config}"
}
```
- Retry: up to 3 attempts, 2s/4s/8s backoff
- On 401: refresh OAuth token and retry once
- **Success:** update audit `SalesforceStatus = Success, SalesforceResponse = Case URL`
- **All attempts fail:** update audit, send failure email with transcript in body, rethrow

### Step 3: Success Email
- Send email: subject includes CaseId, call type, phone
- Body: Salesforce case URL, transcript text, call type mapped
- Update audit: `EmailType = Success, EmailSent = true, FinalStatus = Success`

---

## 7. Authentication & Security

### Production Model (APIM)

```
External Client → APIM (validates Ocp-Apim-Subscription-Key) → Function (Anonymous)
```

All HTTP endpoints use `AuthorizationLevel.Anonymous`. This is correct and intentional — APIM is the auth layer. External callers never see the function URL directly.

**APIM policies to configure:**
- Subscription required: `true`
- Rate limit: 5 calls per minute per subscription
- HTTPS only: `true` (enforce TLS 1.2+)
- IP allowlist: restrict to Cisco UCCX server IP (optional)

### /api/replay — Internal Endpoint

This endpoint is for operations team use only. Recommendations:
- Create a **separate APIM product** with its own subscription key
- Share that key only with the operations team
- Or restrict via IP allowlist to the operations team's network

### Secrets Management (Production)

All sensitive values should be stored in **Azure Key Vault**, not in Function App Settings directly:
- Speech API key
- Salesforce Client ID / Secret
- Email App Password

Reference Key Vault secrets from Function App Settings using:
```
@Microsoft.KeyVault(SecretUri=https://{vault}.vault.azure.net/secrets/{name}/)
```
Enable **System-Assigned Managed Identity** on the Function App and grant Key Vault Secrets Reader role.

### Local Development Security

`local.settings.json` is in `.gitignore` and has `<CopyToPublishDirectory>Never</CopyToPublishDirectory>` — it is never committed to git or deployed to Azure.

---

## 8. Call Type Mapping

Cisco UCCX sends a `callType` code in the metadata JSON (format: `Category|SubCode`). The mapping translates this to Salesforce Case fields.

### Configuration File: `Configuration/CallTypeMappings.json`

```json
{
  "D|FR": {
    "CallType__c": "Delivery / Order Related",
    "CSTproblemReported__c": "Fill Request"
  },
  "FeedBack|CallCenterFeedback": {
    "CallType__c": "Feedback on Bottler Employee",
    "CSTproblemReported__c": "Call Center Feedback"
  },
  "FeedBack|SalesRepFeedback": {
    "CallType__c": "Feedback on Bottler Employee",
    "CSTproblemReported__c": "Sales Rep Feedback"
  },
  "FeedBack|DriverFeedback": {
    "CallType__c": "Feedback on Bottler Employee",
    "CSTproblemReported__c": "Driver Feedback"
  }
}
```

**Rules:**
- Mapping is loaded at startup — no code change needed when client adds new codes
- Unknown codes → `("Unknown", "")` + warning log. Pipeline continues, never crashes.
- New codes: add entries to `CallTypeMappings.json` and redeploy

**Outstanding:** Full mapping Excel from client required to populate all entries.

---

## 9. Retry Logic

### RetryHelper (`Utils/RetryHelper.cs`)

Shared utility used by all three pipeline services (Transcription, Salesforce, Email).

```
Attempt 1 → fail → wait 2s
Attempt 2 → fail → wait 4s
Attempt 3 → fail → StageException thrown
```

Delays: `baseDelayMs × 2^(attempt-1)` (exponential backoff)
Default: 3 attempts, 2000ms base delay

### Queue-Level Retry

`host.json` configures the Azure Functions queue trigger:
```json
"queues": {
  "maxDequeueCount": 5,
  "visibilityTimeout": "00:00:30"
}
```

If the function throws (after all 3 RetryHelper attempts fail), the queue message becomes visible again after 30 seconds and is retried up to 5 times total. After 5 failures, Azure automatically moves the message to `audio-processing-queue-poison` (Dead Letter Queue).

### What Triggers Retry vs No-Retry

| Condition | Retry? |
|---|---|
| HTTP 5xx from transcription/Salesforce API | Yes |
| Network timeout | Yes |
| HTTP 401 (Salesforce) | Yes — refresh token first |
| HTTP 400 / 415 | No — bad request won't fix itself |
| JSON parse error in queue message | No — poison message, dropped immediately |

---

## 10. Audit Log Schema

**Table:** `AudioProcessingLog` in Azure Table Storage

| Column | Type | Example | Set When |
|---|---|---|---|
| **PartitionKey** | String | `2026-04-16` | Initial row |
| **RowKey** | String | `0017V000001s0nwjQAA` (CaseId) | Initial row |
| CallTypeRaw | String | `D\|FR` | Initial row |
| CallTypeMapped | String | `Delivery / Order Related` | Initial row |
| CstProblem | String | `Fill Request` | Initial row |
| Phone | String | `9794920458` | Initial row |
| BlobPath | String | `media/2026-04-16/0017V...wav` | Initial row |
| ReceivedAt | String | `2026-04-16T14:32:00Z` | Initial row |
| FinalStatus | String | `Received` → `Success` / `Failed` | Initial + final |
| TranscriptionStatus | String | `Success` / `Failed` | After transcription |
| TranscriptionAttempts | Int | `3` | After transcription |
| TranscriptionError | String | `503 Service Unavailable` | On failure |
| SalesforceStatus | String | `Success` / `Failed` | After Salesforce |
| SalesforceAttempts | Int | `1` | After Salesforce |
| SalesforceResponse | String | `https://sfdc.../Case/500...` | On success |
| SalesforceError | String | `500 Internal Server Error` | On failure |
| EmailType | String | `Success` / `TranscriptionFailed` / `SalesforceFailed` | After email |
| EmailSent | String | `true` / `false` | After email |
| EmailError | String | SMTP error message | On email failure |
| FailedAtStage | String | `Transcription` / `Salesforce` | On failure |
| RetainUntil | String | `2026-05-16` | Initial row |

**Query by CaseId:** `RowKey eq '0017V000001s0nwjQAA'`
**Query by date:** `PartitionKey eq '2026-04-16'`
**Query by status:** `FinalStatus eq 'Failed'`

---

## 11. Configuration Reference

All settings are stored in **Azure Function App Settings** (production) or `local.settings.json` (local dev only — never committed to git).

| Key | Description | Example |
|---|---|---|
| `AzureWebJobsStorage` | Storage account connection string | `DefaultEndpointsProtocol=https;...` |
| `FUNCTIONS_WORKER_RUNTIME` | Must be `dotnet-isolated` | `dotnet-isolated` |
| `QUEUE_NAME` | Queue name | `audio-processing-queue` |
| `BLOB_CONTAINER_MEDIA` | Blob container name | `media` |
| `TABLE_AUDIT_LOG` | Table name | `AudioProcessingLog` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights connection string | `InstrumentationKey=...` |
| `Pipeline:EnableBlobStorage` | Toggle blob upload | `true` |
| `Pipeline:EnableTranscription` | Toggle transcription stage | `true` |
| `Pipeline:EnableSalesforce` | Toggle Salesforce stage | `true` |
| `Pipeline:EnableEmail` | Toggle email notifications | `true` |
| `Pipeline:EmailOnSuccess` | Send email on success | `true` |
| `Pipeline:EmailOnFailure` | Send email on failure | `true` |
| `Pipeline:TranscriptionEndpoint` | Speech API URL | `https://centralus.stt.speech.microsoft.com/...` |
| `Pipeline:TranscriptionApiKey` | Speech API subscription key | `32-char hex` |
| `Pipeline:TranscriptionApiKeyHeader` | Header name for key | `Ocp-Apim-Subscription-Key` |
| `Pipeline:TranscriptionContentType` | Content-Type for audio POST | `audio/wav; codec=audio/pcm; samplerate=8000` |
| `Pipeline:TranscriptionMaxRetries` | Max retry attempts | `3` |
| `Pipeline:TranscriptionRetryBaseMs` | Base delay ms | `2000` |
| `Pipeline:MinTranscriptionConfidence` | Min confidence (0 = disabled) | `0.0` |
| `Pipeline:SalesforceMaxRetries` | Max retry attempts | `3` |
| `Pipeline:SalesforceRetryBaseMs` | Base delay ms | `2000` |
| `Pipeline:AuditRetentionDays` | Days to keep audit rows | `30` |
| `Salesforce:ClientId` | OAuth client ID | — |
| `Salesforce:ClientSecret` | OAuth client secret | — |
| `Salesforce:TokenUrl` | OAuth token endpoint | `https://login.salesforce.com/services/oauth2/token` |
| `Salesforce:InstanceUrl` | Salesforce org URL | `https://xxx.my.salesforce.com` |
| `Salesforce:RecordTypeId` | Case record type ID | `01217U000000GjgQAG` |
| `Salesforce:DefaultOwnerId` | Case owner (queue/user ID) | 18-char Salesforce ID |
| `Email:SmtpHost` | SMTP server | `smtp.office365.com` |
| `Email:SmtpPort` | SMTP port | `587` |
| `Email:Username` | Sender email | `RCCBAutomate@reyesccb.com` |
| `Email:Password` | App password | — |
| `Email:Recipients` | Comma-separated recipients | `ops@client.com,team@client.com` |

---

## 12. Monitoring

### Application Insights

Application Insights is already configured in the function app. Access via **Azure Portal → Application Insights → appi-audiotx-test**.

**Key views:**
- **Failures** — exceptions and failed requests with stack traces
- **Live Metrics** — real-time request stream
- **Transaction Search** — find a specific CaseId's full trace
- **Logs** — KQL query interface

**Useful KQL Queries:**

```kql
// All failed pipeline runs
traces
| where message contains "Failed" or severityLevel >= 3
| project timestamp, message, severityLevel
| order by timestamp desc

// All CaseIds processed today
traces
| where message contains "CaseId="
| project timestamp, message
| order by timestamp desc

// Transcription failure details
traces
| where message contains "Transcription" and message contains "Failed"
| project timestamp, message
| order by timestamp desc

// Success rate over last 7 days
traces
| where message contains "FinalStatus"
| extend Status = extract("FinalStatus=([A-Za-z]+)", 1, message)
| summarize count() by Status, bin(timestamp, 1d)
```

**Set Up Failure Alert (10 minutes, one-time):**
```
Azure Portal → Application Insights → Alerts → + New Alert Rule
  Signal: Custom log query
  Query: traces | where severityLevel == 3   (Error level)
  Threshold: Count > 0
  Evaluation frequency: Every 5 minutes
  Action group: Email to operations team
```

### Power BI Dashboard

Power BI reads directly from `AudioProcessingLog` table. No extra Azure resource needed.

**Prerequisites:**
- Power BI Desktop (free — download from Microsoft Store)
- Power BI Service access via O365 license (app.powerbi.com)

**Setup Steps:**

1. **Install Power BI Desktop** from Microsoft Store (free)

2. **Connect to Azure Table Storage:**
   - Home → Get Data → More → search "Azure Table Storage" → Connect
   - URL: `https://{storageAccountName}.table.core.windows.net`
   - Account Key: Storage Account → Access keys → key1 → Key
   - Select `AudioProcessingLog` → Load

3. **Transform data (Power Query):**
   - Home → Transform Data
   - Expand `Content` column → select all audit fields
   - Rename `PartitionKey` → `Date`, `RowKey` → `CaseId`
   - Set `Date` column to Date type
   - Close & Apply

4. **Build report visuals:**
   - Page 1 (Summary): Cards for Total / Success / Failed counts, Pie chart by FinalStatus, Bar chart requests per day
   - Page 2 (Detail): Table with all columns, Slicers for Date range and FinalStatus
   - Page 3 (Failures): Filtered table showing only failed cases with error details

5. **Publish:** Home → Publish → My workspace → open at app.powerbi.com

6. **Share with team:** Open report → Share → enter team email addresses

7. **Schedule refresh:** app.powerbi.com → Datasets → AudioProcessingLog → Settings → Scheduled refresh → Hourly

### Dead Letter Queue (DLQ)

Failed messages (after 5 retries) land in `audio-processing-queue-poison`. Monitor via:
- **Azure Portal** → Storage Account → Queues → `audio-processing-queue-poison`
- **Azure Storage Explorer** (free desktop app) — visual browser for all storage resources

To replay a DLQ message: copy the `blobPath` from the message body and call `POST /api/replay`.

---

## 13. Email Notifications

### Email Types

**1. Success Email**
```
Subject: [SUCCESS] D|FR | CaseId: 0017V... | Phone: 979...
Body:
  Salesforce Case: https://sfdc.../Case/500...
  Transcription:   "Here is what the caller said..."
  Call type:       Delivery / Order Related — Fill Request
```

**2. Transcription Failure Email** (WAV file attached)
```
Subject: [FAILED] Stage: Transcription | D|FR | CaseId: 0017V... | Phone: 979...
Body:
  Failed stage:   Transcription
  Attempts:       3 of 3
  Last error:     503 Service Unavailable
  Blob path:      media/2026-04-16/0017V...wav
[Attachment: recording.wav]
```

**3. Salesforce Failure Email**
```
Subject: [FAILED] Stage: Salesforce | D|FR | CaseId: 0017V...
Body:
  Failed stage:     Salesforce
  Attempts:         3 of 3
  Last error:       500 Internal Server Error
  Transcription:    "Here is what the caller said..."
  (Transcription succeeded — manual case creation may be needed)
```

### Email SMTP Requirements From Client

| Item | Key | Notes |
|---|---|---|
| Sender email address | `Email:Username` | Real O365 mailbox |
| App Password | `Email:Password` | NOT the login password — generate in M365 Admin |
| Recipient list | `Email:Recipients` | Comma-separated email addresses |
| SMTP host | `Email:SmtpHost` | Default: `smtp.office365.com` |
| SMTP port | `Email:SmtpPort` | Default: `587` (StartTLS) |

**O365 prerequisite:** M365 Admin Center → Users → {mailbox} → Mail → Manage email apps → enable "Authenticated SMTP"

---

## 14. Error Handling

### What Happens at Each Failure

| Failure | Retry | Email Sent | Audit Updated | Queue Behavior |
|---|---|---|---|---|
| Blob upload fails | No retry (fatal) | No | No | Returns 500 to caller |
| Audit write fails (initial) | No (non-fatal) | No | No | Continues to enqueue |
| Transcription fails (all 3 attempts) | Yes — 3× | Yes — with WAV attached | TranscriptionStatus=Failed | Rethrows → queue retries up to 5× → DLQ |
| Salesforce fails (all 3 attempts) | Yes — 3× | Yes — transcript in body | SalesforceStatus=Failed | Rethrows → queue retries up to 5× → DLQ |
| Email fails to send | No | N/A | EmailSent=false, EmailError=msg | Does not affect main pipeline |
| Bad queue message (parse error) | No | No | No | Dropped (poison message guard) |

---

## 15. Project Structure

```
AudioToTranscript/
├── AudioToTranscript.csproj
├── Program.cs                          ← DI wiring, config loading
├── host.json                           ← maxDequeueCount, queue settings
├── local.settings.json                 ← local dev only — NOT committed
├── local.settings.json.example         ← template — committed
├── .gitignore / .funcignore
│
├── Configuration/
│   ├── PipelineOptions.cs              ← all feature flags + retry config
│   ├── SalesforceOptions.cs            ← SF OAuth + instance settings
│   ├── EmailOptions.cs                 ← SMTP settings
│   └── CallTypeMappings.json           ← callType code → SF fields
│
├── Functions/
│   ├── ReceiveAudioFunction.cs         ← HTTP: POST /api/audio → 202
│   ├── ProcessAudioFunction.cs         ← Queue trigger: full pipeline
│   ├── ReceiveImageFunction.cs         ← HTTP: POST /api/image → 200 sync
│   ├── ReplayFunction.cs               ← HTTP: POST /api/replay → 202
│   └── CleanupFunction.cs              ← Timer: delete expired audit rows
│
├── Services/
│   ├── ITranscriptionService + TranscriptionService.cs
│   ├── ISalesforceService + SalesforceService.cs     ← OAuth2 token cache
│   ├── IBlobService + BlobService.cs
│   ├── IAuditService + AuditService.cs               ← Table Storage R/W
│   └── IEmailService + EmailService.cs               ← MailKit SMTP
│
├── Models/
│   ├── AudioMetadata.cs                ← parsed from multipart metadata JSON
│   ├── ProcessingMessage.cs            ← queue payload: blobPath + metadata
│   ├── ProcessingResult.cs             ← per-stage result accumulated
│   ├── TranscriptionResponse.cs        ← { Text, Confidence }
│   └── CallTypeEntry.cs               ← { CallType__c, CSTproblemReported__c }
│
├── Utils/
│   ├── MultipartParser.cs              ← parse multipart/form-data
│   ├── CallTypeMapper.cs               ← lookup CallTypeMappings dictionary
│   └── RetryHelper.cs                  ← shared exponential backoff
│
├── Docs/
│   └── TechnicalDocument.md            ← this document
│
├── Requests/
│   ├── endpoints.http                  ← VS 2022 HTTP client tests
│   └── Test-Audio.ps1                  ← PowerShell integration test
│
├── TestData/
│   ├── New-AzuriteResources.ps1        ← create local Azurite resources
│   └── New-TestWav.ps1                 ← generate test WAV file
│
└── AudioToTranscript.Tests/
    ├── AudioToTranscript.Tests.csproj
    ├── MultipartParserTests.cs
    ├── CallTypeMapperTests.cs
    ├── RetryHelperTests.cs
    ├── TranscriptionServiceTests.cs
    ├── SalesforceServiceTests.cs
    └── AuditServiceTests.cs
```

---

## 16. Deployment

### Local Development Prerequisites

| Tool | How to Install |
|---|---|
| .NET 8 SDK | https://dot.net — or Visual Studio installer |
| Azure Functions Core Tools v4 | `winget install Microsoft.AzureFunctionsCoreTools` |
| Azurite (local storage emulator) | `npm install -g azurite` |
| VS 2022 with Azure development workload | Visual Studio Installer → Modify → Azure development |

**Local Run:**
```powershell
# Terminal 1 — start Azurite
azurite --location C:/azurite --loose

# Terminal 2 — create local storage resources (first run only)
.\TestData\New-AzuriteResources.ps1

# Terminal 3 — start function
func host start --port 7071

# Terminal 4 — send test request
.\Requests\Test-Audio.ps1
```

### Azure Services Creation (One-Time)

1. Create Resource Group: `rg-audiotranscript-test` (region: Central US)
2. Create Storage Account: `staudiotxtest` (Standard LRS)
   - Copy: Connection String, Account Key, Account Name
3. Create Log Analytics Workspace: `log-audiotx-test`
4. Create Application Insights: `appi-audiotx-test` (linked to workspace)
   - Copy: Connection String
5. Create Function App: `func-audiotranscript-{unique}` (Consumption, .NET 8 Isolated, Windows)
   - Link to Storage Account and Application Insights during creation

### Azure App Settings Configuration

Go to: Function App → Settings → Environment variables → App settings

Add all keys listed in **Section 11 — Configuration Reference** with production values.
Click **Apply → Confirm** to save.

### Deploy from Visual Studio

```
Right-click project → Publish → Azure → Azure Function App (Windows)
→ Select subscription → select function app → Finish → Publish
```

### Deploy from Terminal (CI/CD or re-deploy)

```powershell
func azure functionapp publish {function-app-name} --dotnet-isolated
```

### Post-Deploy Verification

1. Azure Portal → Function App → Functions → verify all 5 are listed
2. Run test: `.\Requests\Test-Audio.ps1 -BaseUrl "https://{func-app}.azurewebsites.net"`
3. Expected: `202 Accepted`
4. Azure Portal → Storage → Tables → `AudioProcessingLog` → verify row created

---

## 17. CI/CD Pipeline (GitHub Actions)

Pipeline file: `.github/workflows/deploy.yml`

**Trigger:** Push to `main` branch

**Steps:**
1. Restore NuGet packages
2. Build (Release)
3. Run unit tests — pipeline fails if any test fails
4. Publish function app
5. Deploy to Azure Function App

**Required GitHub Secret:**
- `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` — download from Azure Portal → Function App → Overview → Get publish profile → upload content as GitHub secret

See `.github/workflows/deploy.yml` for full pipeline definition.

---

## 18. Dependencies From Client — Complete Checklist

### Credentials / Secrets

| Item | Config Key | Status |
|---|---|---|
| Azure Speech API subscription key | `Pipeline:TranscriptionApiKey` | Pending |
| Azure Speech API region | In endpoint URL (`centralus`) | Confirm |
| Salesforce Client ID | `Salesforce:ClientId` | Pending |
| Salesforce Client Secret | `Salesforce:ClientSecret` | Pending |
| Salesforce Instance URL | `Salesforce:InstanceUrl` | Pending |
| Salesforce RecordTypeId | `Salesforce:RecordTypeId` | Partial (`01217U000000GjgQAG`) |
| Salesforce DefaultOwnerId | `Salesforce:DefaultOwnerId` | Pending |
| Email sender address | `Email:Username` | Pending |
| Email App Password | `Email:Password` | Pending |
| Email recipient list | `Email:Recipients` | Pending |

### Data / Configuration

| Item | Used For | Status |
|---|---|---|
| Call type mapping Excel | `CallTypeMappings.json` | Pending |
| Cisco UCCX server IP | APIM IP allowlist | Optional |

### IT / Infrastructure Actions Required From Client

| Action | Owner | Notes |
|---|---|---|
| Enable Authenticated SMTP on sender mailbox | Client IT / M365 Admin | Required for email to work |
| Provision Salesforce sandbox credentials | Salesforce team | Required for Salesforce integration |
| Confirm Speech API region | Client | Currently using `centralus` |
| Provide Cisco UCCX outbound webhook config | Cisco team | Point to APIM URL |

---

## 19. Assumptions

| # | Assumption | Impact if Wrong |
|---|---|---|
| 1 | Cisco UCCX sends audio as `multipart/form-data` with named parts `metadata` and `audio` | Must update `MultipartParser.cs` |
| 2 | Metadata JSON uses field names: `callType`, `caseId`, `phone`, `timestamp`, `brandId` | Must update `AudioMetadata.cs` |
| 3 | Audio files are WAV format (PCM, 8kHz mono) | Transcription Content-Type header may need updating |
| 4 | Salesforce API version is v52.0 | Update URL if client uses different version |
| 5 | Salesforce uses client credentials OAuth flow (no user login) | May need adjustment if SF uses different auth |
| 6 | Email is Office 365 SMTP | Different provider requires config change only |
| 7 | 4 requests/min peak volume (Consumption plan is sufficient) | Premium plan needed if sustained high volume |
| 8 | 30-day audit retention is sufficient | Change `Pipeline:AuditRetentionDays` in config |
| 9 | Call type codes follow `Category|SubCode` format | Separator may differ — update `CallTypeMapper` |
| 10 | Azure Speech API returns `{ "DisplayText": "...", "Confidence": 0.95 }` | May vary by endpoint version |

---

## 20. Outstanding Items

| Item | Blocking | Owner |
|---|---|---|
| Full call type mapping Excel | Salesforce integration | Client |
| Salesforce sandbox credentials | Salesforce integration | SFDC team |
| Salesforce RecordTypeId / OwnerId confirmation | Salesforce integration | SFDC team |
| Speech API subscription key | Transcription stage | Client |
| Email App Password + recipient list | Email notifications | Client IT |
| APIM subscription key generation | Production auth | Infra team |
| Cisco UCCX server IP for allowlist | Production security | Cisco team |
| Confirmation of WAV format (sample rate, channels) | Transcription accuracy | Client / Cisco team |
