# Order Management API

REST API untuk mengelola pesanan, produk, stok, perubahan status pesanan, dan pembatalan pesanan.

## Teknologi

* .NET 8 / ASP.NET Core Web API
* Entity Framework Core
* PostgreSQL
* Swagger / OpenAPI
* xUnit
* Npgsql

## Fitur

* Membuat order
* Mendapatkan order berdasarkan ID
* Menampilkan daftar order dengan filter dan pagination
* Mengubah status order
* Membatalkan order
* Pengurangan stok secara atomic
* Pengembalian stok ketika order dibatalkan
* Idempotent order creation
* Optimistic concurrency control
* Correlation ID untuk tracing request
* Error response menggunakan ProblemDetails
* Automated test untuk validation dan concurrency

## API Endpoint

### Membuat Order

```http
POST /api/orders
Idempotency-Key: <unique-key>
Content-Type: application/json
```

Contoh:

```json
{
  "customerId": "customer-001",
  "shippingAddress": "Bandung, Indonesia",
  "items": [
    {
      "productId": 1,
      "quantity": 2
    }
  ]
}
```

Order baru yang berhasil dibuat mengembalikan `201 Created`.

Jika `Idempotency-Key` yang sama sudah pernah diproses, API mengembalikan order yang sudah ada dengan `200 OK`.

### Mendapatkan Order

```http
GET /api/orders/{id}
```

### Daftar Order

```http
GET /api/orders
```

Parameter yang tersedia:

* `status`
* `customerId`
* `from`
* `to`
* `page`
* `pageSize`

Contoh:

```http
GET /api/orders?status=Confirmed&page=1&pageSize=10
```

Parameter `to` diperlakukan sebagai tanggal kalender yang inklusif.

Contohnya:

```text
to=2026-10-09
```

akan mencakup order yang dibuat sepanjang tanggal 9 Oktober.

### Mengubah Status Order

```http
PATCH /api/orders/{id}/status
Content-Type: application/json
```

Contoh:

```json
{
  "status": "Confirmed"
}
```

Transisi status yang diperbolehkan:

```text
Pending   -> Confirmed | Cancelled
Confirmed -> Shipped   | Cancelled
Shipped   -> Delivered
Delivered -> terminal
Cancelled -> terminal
```

Transisi yang tidak valid mengembalikan `409 Conflict`.

### Membatalkan Order

```http
POST /api/orders/{id}/cancel
```

Order hanya dapat dibatalkan ketika statusnya `Pending` atau `Confirmed`.

Ketika order dibatalkan, stok yang sebelumnya telah dipesan akan dikembalikan.

## Arsitektur

Aplikasi menggunakan struktur layered sederhana:

```text
Controllers
    |
    v
Services
    |
    v
Infrastructure / EF Core
    |
    v
PostgreSQL
```

Struktur utama:

```text
Domain/
    Entities/
    Enums/

DTOs/

Services/
    OrderService
    StockService

Infrastructure/
    Persistence/
    ErrorHandling/
    Middleware/

Migrations/
```

`OrderService` menangani business logic terkait order, sedangkan `StockService` menangani operasi stok secara atomic.

## Strategi Concurrency

Concurrency ditangani pada level database jika memungkinkan.

### 1. Concurrency pada Stok

Pengurangan stok menggunakan atomic database update dengan kondisi stok mencukupi:

```sql
UPDATE Products
SET StockQuantity = StockQuantity - quantity
WHERE Id = productId
  AND StockQuantity >= quantity
```

Operasi hanya berhasil apabila stok yang tersedia mencukupi.

Hal ini mencegah stok menjadi negatif ketika beberapa order mencoba membeli produk yang sama secara bersamaan.

Contoh:

```text
Stok awal: 15

Request A: quantity 10
Request B: quantity 10

Hasil:
- Satu request berhasil
- Satu request gagal karena stok tidak mencukupi
- Stok akhir: 5
```

### 2. Concurrency pada Status Order

Order memiliki field `Version`.

Update status menggunakan `Version` dan status saat ini sebagai bagian dari kondisi update:

```sql
WHERE Id = orderId
  AND Version = expectedVersion
  AND Status = expectedStatus
```

Jika update berhasil, `Version` akan dinaikkan.

Jika request lain sudah lebih dahulu mengubah order, jumlah row yang ter-update menjadi `0` dan API mengembalikan conflict.

Dengan pendekatan ini, dua update yang berjalan bersamaan tidak dapat diam-diam saling menimpa.

### 3. Concurrency pada Idempotency

Pembuatan order membutuhkan header:

```http
Idempotency-Key: <unique-key>
```

Database memiliki unique index pada `IdempotencyKey`.

Alurnya:

```text
Request
  |
  +-- Key sudah ada?
  |       |
  |       +-- Ya -> kembalikan order yang sudah ada
  |
  +-- Tidak
       |
       v
   Buat order
       |
       v
   Unique DB constraint
       |
       +-- Request pertama -> lanjutkan transaksi
       |
       +-- Request concurrent -> gunakan order yang sudah dibuat
```

Unique constraint pada database menjadi perlindungan terakhir ketika dua request dengan key yang sama masuk secara bersamaan.

## Strategi Transaction

Pembuatan order dilakukan di dalam database transaction.

Transaction mencakup:

1. Membuat order
2. Mengurangi stok
3. Membuat order items
4. Commit transaction

Jika salah satu proses gagal, transaction tidak di-commit.

Pembatalan order juga menggunakan transaction yang mencakup:

1. Validasi status dan version order
2. Mengubah status menjadi `Cancelled`
3. Menaikkan `Version`
4. Mengembalikan stok
5. Commit transaction

## Race Condition yang Dipertimbangkan

### Concurrent Order Creation

Dua customer dapat mencoba membeli produk yang sama secara bersamaan.

Atomic stock update memastikan stok tidak menjadi negatif.

### Concurrent Status Update

Dua administrator dapat mencoba mengubah status order yang sama secara bersamaan.

Pengecekan `Version` memastikan hanya satu request yang berhasil melakukan update dari version yang sama.

### Concurrent Idempotent Request

Dua request POST dengan `Idempotency-Key` yang sama dapat masuk sebelum salah satu transaction selesai.

Unique constraint pada database memastikan hanya satu order yang dibuat.

### Concurrent Cancellation

Dua request dapat mencoba membatalkan order yang sama secara bersamaan.

Pengecekan `Version` memastikan hanya satu request yang berhasil melakukan cancellation sehingga stok tidak dikembalikan dua kali.

### Cancel vs Status Update

Pembatalan dan perubahan status dapat terjadi secara bersamaan.

Kondisi `Version` dan status memastikan update hanya dilakukan apabila state order yang diharapkan masih berlaku.

Beberapa hasil dapat merupakan urutan bisnis yang valid, misalnya:

```text
Pending -> Confirmed -> Cancelled
```

Karena itu, hasil race ditentukan oleh operasi valid yang berhasil lebih dahulu, bukan otomatis dianggap sebagai error.

## Error Handling

API menggunakan global exception handler dan mengembalikan response dalam format `ProblemDetails`.

Status code utama:

| Status | Keterangan                                   |
| ------ | -------------------------------------------- |
| `200`  | Request berhasil / existing idempotent order |
| `201`  | Order berhasil dibuat                        |
| `400`  | Request tidak valid                          |
| `404`  | Order atau resource tidak ditemukan          |
| `409`  | Business conflict atau concurrency conflict  |
| `500`  | Unexpected server error                      |

Contoh response:

```json
{
  "type": null,
  "title": "Conflict",
  "status": 409,
  "detail": "Insufficient stock for product 1.",
  "instance": "/api/orders"
}
```

## Logging dan Correlation ID

Aplikasi menggunakan `ILogger` untuk logging dan tidak menggunakan `Console.WriteLine`.

Client dapat mengirim:

```http
X-Correlation-ID: <id>
```

Jika tidak diberikan, API akan membuat correlation ID baru.

Correlation ID dikembalikan melalui response header dan digunakan sebagai logging scope sehingga request dapat ditelusuri melalui application log.

## Database

PostgreSQL digunakan sebagai database persistence.

EF Core migrations berada di:

```text
Migrations/
```

Untuk menjalankan migration:

```powershell
dotnet ef database update
```

Connection string dikonfigurasi melalui application configuration.

Automated test menggunakan database PostgreSQL terpisah dan connection string dari:

```text
OrderManagement.Api.Tests/appsettings.Test.json
```

Test fixture tidak lagi menggunakan connection string yang di-hardcode.

## Testing

Automated test mencakup:

* Order creation
* Stock deduction
* Input validation
* Pagination validation
* Invalid status validation
* Concurrent stock deduction
* Concurrent status update
* Concurrent idempotent order creation
* Concurrent cancellation

Menjalankan test:

```powershell
cd C:\dotnet\OrderManagement.Api.Tests
dotnet test
```

Status test saat ini:

```text
15 tests
15 passed
0 failed
```

## Menjalankan API

Dari folder API:

```powershell
cd C:\dotnet\OrderManagement.Api
dotnet watch --launch-profile https
```

Development HTTPS profile menggunakan:

```text
https://localhost:7090
```

Swagger tersedia di:

```text
https://localhost:7090/swagger
```

## Konfigurasi

Development configuration:

```text
appsettings.Development.json
```

Test configuration:

```text
OrderManagement.Api.Tests/appsettings.Test.json
```

Connection string test dibaca dari configuration agar tidak perlu disimpan langsung di source code fixture.

## Catatan Desain

### Mengapa menggunakan atomic stock update?

Pendekatan read-then-write biasa dapat menimbulkan race condition:

```text
Read stock
Check stock
Write stock
```

Dua request dapat membaca nilai stok yang sama sebelum salah satunya melakukan update.

Dengan atomic conditional update, pengecekan stok dan pengurangan dilakukan sebagai satu operasi database.

### Mengapa menggunakan Version?

Field `Version` digunakan sebagai optimistic concurrency control yang eksplisit.

Update hanya berhasil apabila version yang dibaca request masih merupakan version terbaru.

### Mengapa menggunakan unique constraint untuk idempotency?

Pengecekan di application layer saja tidak cukup untuk request concurrent karena dua request dapat sama-sama melihat bahwa key belum tersedia.

Unique constraint pada database menjadi mekanisme final untuk mencegah duplicate order.

## Status Project

Implementasi mencakup kebutuhan utama technical test, termasuk:

* REST API
* PostgreSQL persistence
* Order management
* Stock management
* Idempotency
* Concurrency control
* Transaction handling
* Error handling
* Logging
* Correlation ID
* Automated testing
