shader_type canvas_item;
// Draws one celestial body inside a screen rectangle (#71-#74): its disc at true scale, the pixel surface, the side lit
// by its star, craters with the material they expose, and ore veins at surveyed sites. Filled in by scripts/core/Main.Bodies.cs.
// View only: nothing here changes the simulation. Positions are metres from the body's centre.

const int SOLID = 0;
const int GAS = 1;
const int STAR = 2;
const float TILE = 128.0;
const int MAX_CRATERS = 64;
const int MAX_SITES = 24;

uniform int kind = 0;
uniform sampler2D surface : filter_nearest, repeat_enable;
uniform sampler2D veins : filter_nearest, repeat_enable;
uniform vec3 surface_mean = vec3(0.5);
uniform vec3 base_color = vec3(0.5);
uniform vec2 centre_px;
uniform float ppm = 1.0;        // screen pixels per metre
uniform float radius_m = 100.0; // drawn radius
uniform vec2 light_dir = vec2(1.0, 0.0); // towards the star
uniform vec2 tile_offset;       // whole texels, differs per body
uniform int crater_count = 0;
uniform vec4 craters[MAX_CRATERS];        // x, y, radius (m)
uniform vec4 crater_colors[MAX_CRATERS];  // material at the bottom
uniform int site_count = 0;
uniform vec4 site_ore[MAX_SITES];   // colour; a = 1 when surveyed with ore
uniform vec4 site_ore2[MAX_SITES];  // second colour (the same if there is only one)

uniform bool has_flora = false;      // native plants (#92)
uniform float site_flora[MAX_SITES]; // cover 0..1
uniform vec3 flora_color = vec3(0.3, 0.6, 0.25);

varying vec2 at_px;

void vertex() {
	at_px = VERTEX;
}

float bayer(vec2 cell) {
	ivec2 c = ivec2(mod(cell, 4.0));
	int v = (((c.x ^ c.y) & 1) << 3) | ((c.y & 1) << 2) | (((c.x ^ c.y) & 2)) | ((c.y & 2) >> 1);
	return (float(v) + 0.5) / 16.0;
}

// How much a texel size shows: not when smaller than a screen pixel, nor when blown up to huge blocks.
float shows(float texel_m) {
	float px = texel_m * ppm;
	return smoothstep(0.6, 1.5, px) * (1.0 - smoothstep(14.0, 34.0, px));
}

vec3 tile(sampler2D tex, vec2 p, float texel_m) {
	return textureLod(tex, (floor(p / texel_m) + 0.5 + tile_offset) / TILE, 0.0).rgb;
}

float hash(vec2 c) {
	return fract(sin(dot(mod(c, 512.0), vec2(12.9898, 78.233))) * 43758.5453);
}

// Smooth noise that doesn't repeat over a body.
float patches(vec2 p) {
	vec2 c = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(c), hash(c + vec2(1.0, 0.0)), f.x), mix(hash(c + vec2(0.0, 1.0)), hash(c + vec2(1.0, 1.0)), f.x), f.y);
}

// The surface tile, in patches of two arrangements blended into each other, so the repeat doesn't show.
vec3 varied(vec2 p, float texel_m) {
	vec2 t = floor(p / texel_m);
	float pick = smoothstep(0.4, 0.6, patches((t + 0.5) / 37.0));
	vec3 a = textureLod(surface, (t + 0.5 + tile_offset) / TILE, 0.0).rgb;
	vec3 b = textureLod(surface, (vec2(-t.y, t.x) + vec2(53.5, 19.5) + tile_offset) / TILE, 0.0).rgb;
	return mix(a, b, pick);
}

vec3 ground(vec2 p) {
	vec3 c = vec3(1.0);
	float w = shows(1.0);
	if (w > 0.0) c *= mix(vec3(1.0), varied(p, 1.0) / surface_mean, w * 0.8);
	w = shows(8.0) * (1.0 - 0.4 * shows(64.0));
	if (w > 0.0) c *= mix(vec3(1.0), varied(p, 8.0) / surface_mean, w * 0.8);
	w = shows(64.0);
	if (w > 0.0) c *= mix(vec3(1.0), varied(p, 64.0) / surface_mean, w * 0.55);
	return c;
}

float vein_at(vec2 p) {
	float w0 = shows(1.0), w1 = shows(8.0), w2 = shows(64.0);
	float m = 0.0;
	if (w0 > 0.0) m = max(m, w0 * tile(veins, p, 1.0).r);
	if (w1 > 0.0) m = max(m, w1 * tile(veins, p, 8.0).r);
	if (w2 > 0.0) m = max(m, w2 * tile(veins, p, 64.0).r);
	return max(m, 0.45 * (1.0 - clamp(w0 + w1 + w2, 0.0, 1.0))); // too far out for veins: a plain tint
}

vec3 solid(vec2 p, vec2 cell) {
	vec3 bulk = base_color * ground(p);
	vec3 col = bulk;

	// Native plants (#92): patches over the ground, thicker where a site's cover is higher, blending between sites.
	if (has_flora && site_count > 0) {
		float at = clamp((atan(p.y, p.x) + PI) / TAU, 0.0, 0.99999) * float(site_count) - 0.5;
		int low = (int(floor(at)) + site_count) % site_count;
		float cover = mix(site_flora[low], site_flora[(low + 1) % site_count], fract(at));
		float grown = patches(p / 420.0) * 0.55 + patches(p / 90.0 + 7.0) * 0.45;
		if (cover > 0.0 && grown + (bayer(cell) - 0.5) * 0.1 < 0.2 + 0.6 * cover) col = mix(col, flora_color * ground(p), 0.8);
	}

	// Ore veins near the rim of surveyed sites (#74). Unsurveyed ground shows nothing.
	if (site_count > 0) {
		float band = clamp(radius_m * 0.18, min(40.0, radius_m), 600.0);
		float depth = (radius_m - length(p)) / band;
		if (depth < 1.0) {
			float turn = clamp((atan(p.y, p.x) + PI) / TAU, 0.0, 0.99999);
			int site = clamp(int(floor(turn * float(site_count))), 0, site_count - 1);
			if (site_ore[site].a > 0.5 && bayer(cell) < 1.15 - depth) {
				vec2 patch_cell = floor(p / (band * 0.25));
				float pick = fract(sin(dot(patch_cell, vec2(12.9898, 78.233))) * 43758.5453);
				vec3 ore = pick < 0.6 ? site_ore[site].rgb : site_ore2[site].rgb;
				col = mix(col, ore * 1.2, 0.85 * vein_at(p));
			}
		}
	}

	// Craters (#72): the crater this point is deepest inside wins, so pits that merge share one rim.
	float best = 10.0;
	int which = -1;
	for (int i = 0; i < crater_count; i++) {
		float q = length(p - craters[i].xy) / craters[i].z;
		if (q < best) { best = q; which = i; }
	}
	if (which >= 0 && best < 1.12) {
		vec2 out_dir = (p - craters[which].xy) / max(craters[which].z * best, 0.001);
		float facing = dot(out_dir, light_dir); // +1 on the side nearest the star
		facing = floor(facing * 3.0 + bayer(cell)) / 3.0; // shaded in dithered steps, like the rest
		float px = craters[which].z * ppm;      // small craters: just a dark pit
		float detail = smoothstep(4.0, 10.0, px);
		vec3 exposed = mix(bulk, crater_colors[which].rgb * mix(vec3(1.0), ground(p), 0.6), crater_colors[which].a);
		if (best >= 1.0) { // raised rim: bright where it faces the star, dark behind
			col *= 1.0 + detail * 0.28 * facing;
		} else if (best > 0.6) { // wall: the side nearest the star is in shadow, the far side lit
			col = mix(bulk, exposed, 0.5) * (0.68 - 0.24 * detail * facing);
		} else { // floor, with the wall's shadow falling across the near side
			float shadow = dot(p - craters[which].xy, light_dir) / craters[which].z;
			col = exposed * (detail > 0.5 && shadow > 0.12 + 0.2 * bayer(cell) ? 0.58 : 0.85);
		}
	}

	// The side towards the star is lit, in four steps with a little dithering between them.
	vec2 n = p / radius_m;
	float lit = dot(n, light_dir) * 0.9 + sqrt(max(0.0, 1.0 - dot(n, n))) * 0.35;
	float level = clamp(lit * 0.5 + 0.5, 0.0, 1.0) * 4.0;
	float step_at = floor(level) + step(bayer(cell) * 0.35 + 0.65, fract(level));
	col *= mix(0.42, 1.08, step_at / 4.0);
	return col;
}

vec3 gas(vec2 p, vec2 cell) {
	// Bands across the disc, wrapped round the ball, drifting slowly; finer cloud the closer you are.
	vec2 n = p / radius_m;
	float big = radius_m / 48.0;
	vec2 s = (floor(p / max(big, 0.6 / ppm)) + 0.5) * max(big, 0.6 / ppm) / radius_m;
	s = clamp(s, vec2(-0.999), vec2(0.999));
	float across = asin(clamp(s.x / sqrt(1.0 - s.y * s.y), -1.0, 1.0)) / PI;
	float lane = floor(s.y * 8.0);
	float drift = (4.0 + 3.0 * mod(lane * 5.0, 4.0)) * TIME / 3600.0; // whole turns an hour: TIME wraps without a jump
	vec3 c = textureLod(surface, vec2(across * 0.5 + drift, s.y * 0.5 + 0.5) + tile_offset / TILE, 0.0).rgb;
	vec3 col = mix(vec3(1.0), c / surface_mean, 0.75 - 0.55 * smoothstep(24.0, 160.0, big * ppm)); // fades before it turns into huge blocks
	float w = shows(64.0);
	if (w > 0.0) col *= mix(vec3(1.0), tile(surface, p * vec2(0.25, 1.0), 64.0) / surface_mean, 0.55 * w);
	w = shows(8.0);
	if (w > 0.0) col *= mix(vec3(1.0), tile(surface, p * vec2(0.25, 1.0), 8.0) / surface_mean, 0.45 * w);
	w = shows(1.0);
	if (w > 0.0) col *= mix(vec3(1.0), tile(surface, p * vec2(0.25, 1.0), 1.0) / surface_mean, 0.35 * w);
	col *= base_color;
	float lit = dot(n, light_dir) * 0.9 + sqrt(max(0.0, 1.0 - dot(n, n))) * 0.35;
	float level = clamp(lit * 0.5 + 0.5, 0.0, 1.0) * 4.0;
	float step_at = floor(level) + step(bayer(cell) * 0.35 + 0.65, fract(level));
	return col * mix(0.42, 1.08, step_at / 4.0);
}

void fragment() {
	vec2 exact = (at_px - centre_px) / ppm;
	// Everything but the outline is worked out on a pixel grid fixed to the body: 1 m up close, coarser further out.
	float texel = max(1.0, exp2(ceil(log2(2.0 / ppm))));
	vec2 cell = floor(exact / texel);
	vec2 p = (cell + 0.5) * texel;
	float from_centre = length(exact);
	float edge = clamp((radius_m - from_centre) * ppm + 0.5, 0.0, 1.0); // the real outline
	if (length(p) > radius_m) p *= radius_m * 0.9999 / length(p);

	if (kind == STAR) {
		float q = from_centre / radius_m;
		// A slow pulse: TIME wraps at an hour, so a whole number of beats.
		float pulse = 0.5 + 0.5 * sin(TIME * TAU * 600.0 / 3600.0);
		if (q <= 1.0) {
			vec3 hot = base_color * ground(p);
			float limb = sqrt(max(0.0, 1.0 - q * q));
			float centre_glow = floor(limb * 5.0 + bayer(cell) * 0.8) / 5.0;
			COLOR = vec4(mix(hot * 0.92, mix(hot, vec3(1.0), 0.55), centre_glow), 1.0);
		} else {
			float reach = 0.55 + 0.06 * pulse; // the corona: dithered, with uneven streamers
			float a = atan(exact.y, exact.x);
			float streamer = 0.5 + 0.28 * sin(a * 7.0 + TIME * TAU * 60.0 / 3600.0) + 0.22 * sin(a * 13.0 - TIME * TAU * 90.0 / 3600.0);
			float glow = 1.0 - (q - 1.0) / (reach * (0.7 + 0.5 * streamer));
			float bands = floor(clamp(glow, 0.0, 1.0) * 5.0 + bayer(floor(at_px / 2.0))) / 5.0;
			COLOR = vec4(base_color, 0.55 * bands * bands);
		}
	} else if (edge <= 0.0) {
		COLOR = vec4(0.0);
	} else if (kind == GAS) {
		COLOR = vec4(gas(p, cell), edge);
	} else {
		COLOR = vec4(solid(p, cell), edge);
	}
}
