# geldim-test.ledajans.com

Uygulama Kestrel ile yalnızca `127.0.0.1:5001` üzerinde dinler. Plesk nginx, TLS’i bu alan adında bitirir ve isteği Kestrel’e iletir. Genel IP adresi bu pakette sabitlenmez.

Canlı `geldim.ledajans.com` Windows/IIS akışına ve `scripts/vps/kurulum.sh` dosyasına dokunulmaz. O script nginx varsayılan sitesini sildiği için aynı makinedeki diğer siteleri bozar.

## Veritabanı

Boş veritabanını bir kez oluşturun. Tablolar ilk açılışta migration ile gelir. Canlı siteden dump alınmaz.

```sql
CREATE DATABASE IF NOT EXISTS LedajansTest CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'geldimtest'@'localhost' IDENTIFIED BY 'BURAYA-SIFRE';
GRANT ALL ON LedajansTest.* TO 'geldimtest'@'localhost';
FLUSH PRIVILEGES;
```

## Dosyalar

1. Yayın çıktısını `/var/www/geldim-test` altına koyun. Dizin `www-data` tarafından okunabilsin.
2. `env.example` dosyasını `/etc/geldim-test.env` yapın. `Jwt__Key` en az 32 karakter olsun. Şifre ve anahtar bu depoda durmaz.
3. `geldim-test.service` dosyasını `/etc/systemd/system/` altına koyun, sonra `systemctl enable --now geldim-test`.
4. Plesk’te yalnızca `geldim-test.ledajans.com` için `nginx-plesk-directives.conf` içeriğini Additional nginx directives alanına yapıştırın. Bu alan adında nginx, Apache’ye değil Kestrel’e gitsin. SSL Plesk’te kalır.

Kontrol: `https://geldim-test.ledajans.com/health` ve `/login`.
