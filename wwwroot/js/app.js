window.ledajansGeo = {
    /**
     * @param {object} options
     * @param {string} options.mode - 'preview' | 'checkin'
     * @param {number} options.idealAccuracyMeters
     * @param {number} options.maxAccuracyMeters
     * @param {number} options.timeoutMs
     */
    getCurrentPosition: function (options) {
        if (typeof options === 'number') {
            options = { maxAccuracyMeters: options };
        }
        options = options || {};

        const isPreview = options.mode === 'preview';
        const isMobile = /Android|iPhone|iPad|iPod|Mobile|webOS|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);
        const isDesktopCheckin = !isPreview && !isMobile;

        const idealAccuracy = options.idealAccuracyMeters ?? (isPreview ? 60 : (isDesktopCheckin ? 80 : 40));
        const maxAccuracy = options.maxAccuracyMeters ?? (isPreview ? 250 : (isDesktopCheckin ? 250 : 120));
        const timeoutMs = options.timeoutMs ?? (isPreview ? 10000 : (isDesktopCheckin ? 45000 : 35000));
        const geoOptions = {
            enableHighAccuracy: true,
            maximumAge: isPreview ? 0 : (isDesktopCheckin ? 0 : 15000),
            timeout: isPreview ? Math.min(timeoutMs, 12000) : Math.min(timeoutMs, 20000)
        };

        const spreadMeters = (a, b) => {
            const dLat = (b.latitude - a.latitude) * 111320;
            const dLng = (b.longitude - a.longitude) * 111320 * Math.cos(a.latitude * Math.PI / 180);
            return Math.sqrt(dLat * dLat + dLng * dLng);
        };

        return new Promise((resolve, reject) => {
            if (!navigator.geolocation) {
                reject("Tarayıcınız konum servisini desteklemiyor.");
                return;
            }

            const startTime = Date.now();
            let best = null;
            let watchId = null;
            let pollId = null;
            let settled = false;
            const readings = [];

            const toResult = (p) => ({
                latitude: p.latitude,
                longitude: p.longitude,
                accuracy: p.accuracy,
                lowAccuracy: p.accuracy > idealAccuracy
            });

            const timer = setTimeout(() => {
                if (!best) {
                    finish(() => reject("Konum alınamadı. GPS açık ve açık alanda tekrar deneyin."));
                    return;
                }
                finish(() => resolve(toResult(best)));
            }, timeoutMs);

            const finish = (fn) => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                if (pollId !== null) clearInterval(pollId);
                if (watchId !== null) navigator.geolocation.clearWatch(watchId);
                fn();
            };

            const onError = (err) => {
                if (best) {
                    finish(() => resolve(toResult(best)));
                    return;
                }
                let msg = "Konum alınamadı.";
                if (err.code === 1) msg = "Konum izni reddedildi. Lütfen tarayıcı ayarlarından izin verin.";
                else if (err.code === 2) msg = "Konum bilgisi şu an kullanılamıyor. GPS açık ve açık alanda olun.";
                else if (err.code === 3 && best && best.accuracy <= maxAccuracy) {
                    finish(() => resolve(toResult(best)));
                    return;
                } else if (err.code === 3) {
                    msg = "Konum isteği zaman aşımına uğradı. Tekrar deneyin.";
                }
                finish(() => reject(msg));
            };

            const isAccuracyStable = () => {
                if (readings.length < 2) return false;
                const recent = readings.slice(-3);
                const accSpread = Math.max(...recent.map(r => r.accuracy)) - Math.min(...recent.map(r => r.accuracy));
                return accSpread <= 25;
            };

            const isClusterStable = (maxSpreadMeters, maxAccuracyMeters) => {
                if (readings.length < 2 || !best) return false;
                const recent = readings.slice(-3);
                const centerLat = recent.reduce((s, r) => s + r.latitude, 0) / recent.length;
                const centerLng = recent.reduce((s, r) => s + r.longitude, 0) / recent.length;
                const center = { latitude: centerLat, longitude: centerLng };
                const maxDist = Math.max(...recent.map(r => spreadMeters(center, r)));
                return maxDist <= maxSpreadMeters && best.accuracy <= maxAccuracyMeters;
            };

            const shouldAcceptNow = () => {
                if (!best) return false;
                const elapsed = Date.now() - startTime;
                const acc = best.accuracy;

                if (isPreview) {
                    return elapsed >= 2000 && acc <= 180;
                }

                // Telefon: iyi sinyal → hızlı kabul
                if (acc <= 25) return elapsed >= 800;
                if (acc <= 40) return elapsed >= 1500;
                if (acc <= 60) return elapsed >= 2500;
                if (acc <= 80 && isAccuracyStable()) return elapsed >= 3000;
                if (acc <= 100) return elapsed >= 4500;

                // Masaüstü: ölçümler aynı noktada kümeleniyorsa erken kabul (Wi-Fi sabitlendi)
                if (isDesktopCheckin) {
                    if (isClusterStable(18, acc) && elapsed >= 3500) return true;
                    if (isClusterStable(25, 160) && elapsed >= 5500) return true;
                    if (isClusterStable(35, 220) && elapsed >= 8000) return true;
                    if (acc <= 130 && isAccuracyStable() && elapsed >= 6000) return true;
                }

                return false;
            };

            const tryAccept = () => {
                if (shouldAcceptNow()) finish(() => resolve(toResult(best)));
            };

            const consider = (pos) => {
                const candidate = {
                    latitude: pos.coords.latitude,
                    longitude: pos.coords.longitude,
                    accuracy: pos.coords.accuracy
                };
                if (!Number.isFinite(candidate.latitude) || !Number.isFinite(candidate.longitude)) return;

                readings.push(candidate);
                if (!best || candidate.accuracy < best.accuracy) best = candidate;
                tryAccept();
            };

            pollId = setInterval(tryAccept, 350);

            navigator.geolocation.getCurrentPosition(consider, () => { /* watch devam */ }, geoOptions);
            watchId = navigator.geolocation.watchPosition(consider, onError, geoOptions);
        });
    }
};

window.ledajansMap = {
    _map: null,
    _marker: null,
    _circle: null,
    _ref: null,

    init: function (elementId, lat, lng, radius, dotNetRef) {
        this._ref = dotNetRef;
        const el = document.getElementById(elementId);
        if (!el) return;

        if (this._map) {
            this._map.remove();
            this._map = null;
        }

        this._map = L.map(elementId).setView([lat, lng], 16);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '© OpenStreetMap'
        }).addTo(this._map);

        this._marker = L.marker([lat, lng], { draggable: true }).addTo(this._map);
        this._circle = L.circle([lat, lng], { radius: radius, color: '#f46f2c', fillColor: '#f46f2c', fillOpacity: 0.12 }).addTo(this._map);

        const self = this;
        this._map.on('click', function (e) {
            self._setPoint(e.latlng.lat, e.latlng.lng);
        });
        this._marker.on('dragend', function (e) {
            const p = e.target.getLatLng();
            self._setPoint(p.lat, p.lng);
        });

        setTimeout(() => self._map.invalidateSize(), 200);
    },

    _setPoint: function (lat, lng) {
        this._marker.setLatLng([lat, lng]);
        this._circle.setLatLng([lat, lng]);
        if (this._ref) this._ref.invokeMethodAsync('OnMapClick', lat, lng);
    },

    setView: function (lat, lng, radius) {
        if (!this._map || !this._marker || !this._circle) return;
        const parsedLat = Number(lat);
        const parsedLng = Number(lng);
        if (!Number.isFinite(parsedLat) || !Number.isFinite(parsedLng)) return;
        this._marker.setLatLng([parsedLat, parsedLng]);
        this._circle.setLatLng([parsedLat, parsedLng]);
        this._circle.setRadius(radius);
        this._map.setView([parsedLat, parsedLng], this._map.getZoom() || 16);
    },

    updateRadius: function (radius) {
        if (this._circle) this._circle.setRadius(radius);
    }
};

window.ledajansDownload = function (fileName, contentType, base64) {
    const link = document.createElement('a');
    link.href = `data:${contentType};base64,${base64}`;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

window.ledajansDevice = {
    storageKey: 'ledajans_device_id',

    getOrCreateId: function () {
        try {
            let id = localStorage.getItem(this.storageKey);
            if (id && id.length >= 16 && id.length <= 128) return id;

            id = crypto.randomUUID();
            localStorage.setItem(this.storageKey, id);
            return id;
        } catch {
            return null;
        }
    }
};
