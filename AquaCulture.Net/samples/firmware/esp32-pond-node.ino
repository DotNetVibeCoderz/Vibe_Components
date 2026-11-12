// AquaCulture.Net — ESP32 pond sensor node (Arduino core + PubSubClient).
// Publishes temperature (DS18B20), dissolved oxygen, pH and battery to
//   aquaculture/<farm>/<pond>/<device>/telemetry   as   {"temp":29.10,"do":5.20,"ph":7.85,"bat":3.91}
// and listens on aquaculture/<farm>/cmd/<device> for {"action":"aerator_on"} / {"action":"aerator_off"}.
// Made by Gravicode Studios, led by Kang Fadhil. MIT licence.

#include <WiFi.h>
#include <PubSubClient.h>
#include <OneWire.h>
#include <DallasTemperature.h>

const char* WIFI_SSID = "your-ssid";
const char* WIFI_PASS = "your-password";
const char* MQTT_HOST = "192.168.1.10";
const char* FARM = "farm1";
const char* POND = "K-01";
const char* DEVICE = "esp32-k01";

const int PIN_DS18B20 = 4;      // water temperature probe
const int PIN_DO = 34;          // analog DO probe (galvanic, via signal converter 0–3 V)
const int PIN_PH = 35;          // analog pH probe
const int PIN_BAT = 36;         // battery divider (1:2)
const int PIN_AERATOR = 26;     // relay for the paddle-wheel contactor

// Two-point calibrations (edit after calibrating with standards)
const float DO_SLOPE = 3.20f, DO_OFFSET = 0.0f;     // mg/L per volt at the sampling temperature
const float PH_SLOPE = -5.70f, PH_OFFSET = 21.34f;  // from pH 4.01 and 7.00 buffers

WiFiClient net;
PubSubClient mqtt(net);
OneWire oneWire(PIN_DS18B20);
DallasTemperature tempSensor(&oneWire);
char topic[96], cmdTopic[96];

float volts(int pin) { return analogReadMilliVolts(pin) / 1000.0f; }

void onCommand(char* t, byte* payload, unsigned int len) {
  String s; for (unsigned int i = 0; i < len; i++) s += (char)payload[i];
  if (s.indexOf("aerator_on") >= 0) digitalWrite(PIN_AERATOR, HIGH);
  if (s.indexOf("aerator_off") >= 0) digitalWrite(PIN_AERATOR, LOW);
}

void connect() {
  while (WiFi.status() != WL_CONNECTED) { WiFi.begin(WIFI_SSID, WIFI_PASS); delay(3000); }
  while (!mqtt.connected()) {
    if (mqtt.connect(DEVICE)) mqtt.subscribe(cmdTopic); else delay(2000);
  }
}

void setup() {
  pinMode(PIN_AERATOR, OUTPUT);
  tempSensor.begin();
  snprintf(topic, sizeof topic, "aquaculture/%s/%s/%s/telemetry", FARM, POND, DEVICE);
  snprintf(cmdTopic, sizeof cmdTopic, "aquaculture/%s/cmd/%s", FARM, DEVICE);
  mqtt.setServer(MQTT_HOST, 1883);
  mqtt.setCallback(onCommand);
}

void loop() {
  connect();
  mqtt.loop();
  static unsigned long last = 0;
  if (millis() - last < 60000) return;
  last = millis();
  tempSensor.requestTemperatures();
  float t = tempSensor.getTempCByIndex(0);
  float dox = DO_SLOPE * volts(PIN_DO) + DO_OFFSET;
  float ph = PH_SLOPE * volts(PIN_PH) + PH_OFFSET;
  float bat = volts(PIN_BAT) * 2.0f;
  char json[128];
  snprintf(json, sizeof json, "{\"temp\":%.2f,\"do\":%.2f,\"ph\":%.2f,\"bat\":%.2f}", t, dox, ph, bat);
  mqtt.publish(topic, json);
}
