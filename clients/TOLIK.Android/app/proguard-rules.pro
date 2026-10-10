# TOLIK Android thin client — R8 rules.
# Release builds minify; the JSON layer relies on reflection via Gson, so the
# contract data classes must keep their field names.

# Gson: keep the transport model classes and their (Java) fields.
-keep class com.tolik.client.** {
    <fields>;
}

# Gson generic type tokens (TypeToken subclasses are synthesized).
-keep class com.google.gson.reflect.TypeToken { *; }
-keep class * extends com.google.gson.reflect.TypeToken

# OkHttp: keep optional platform probes referenced reflectively.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
