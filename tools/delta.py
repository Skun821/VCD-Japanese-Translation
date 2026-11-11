"""Binary delta for VCD Japanese installer.

Format (little endian):
  magic  b'VCDP1\\0'
  u64 src_len, 32B src_sha256, u64 tgt_len, 32B tgt_sha256
  zlib stream of ops:
    0x01 COPY  u64 src_off  u32 len
    0x02 ADD   u32 len  <len bytes>
    0x00 END
"""
import hashlib, struct, sys, zlib
import numpy as np

MAGIC = b'VCDP1\0'
BLOCK = 32
MINCOPY = 48

def _win_hash(a, n_windows):
    """64-bit hash of every 32-byte window a[i:i+32] for i in [0, n_windows)."""
    out = np.empty(n_windows, np.uint64)
    P = [np.uint64(0x9E3779B97F4A7C15), np.uint64(0xC2B2AE3D27D4EB4F),
         np.uint64(0x165667B19E3779F9), np.uint64(0x27D4EB2F165667C5)]
    with np.errstate(over='ignore'):
        for k in range(8):
            if k >= len(a): break
            m = (len(a) - k) // 8
            if m < 4: continue
            w = np.frombuffer(a[k:k + m * 8].tobytes(), dtype='<u8')
            h = w[:-3] * P[0] ^ w[1:-2] * P[1] ^ w[2:-1] * P[2] ^ w[3:] * P[3]
            idx = k + 8 * np.arange(len(h), dtype=np.int64)
            ok = idx < n_windows
            out[idx[ok]] = h[ok]
    return out

def _runs(mask):
    """start/end pairs of True runs."""
    d = np.diff(np.concatenate(([0], mask.astype(np.int8), [0])))
    return np.flatnonzero(d == 1), np.flatnonzero(d == -1)

def make_delta(src, tgt):
    S = np.frombuffer(src, np.uint8); T = np.frombuffer(tgt, np.uint8)
    ops = []  # ('C', off, len) / ('A', start, end) target ranges
    # A: same-offset identical runs
    m = min(len(S), len(T))
    eq = S[:m] == T[:m]
    st, en = _runs(eq)
    keep = (en - st) >= 64
    copies = [(int(a), int(b)) for a, b in zip(st[keep], en[keep])]
    gaps = []; pos = 0
    for a, b in copies:
        if a > pos: gaps.append((pos, a))
        ops.append(('C', a, a, b - a)); pos = b
    if pos < len(T): gaps.append((pos, len(T)))
    # B: block matching inside gaps
    if gaps and len(S) >= BLOCK:
        nb = len(S) // BLOCK
        w = np.frombuffer(S[:nb * BLOCK].tobytes(), dtype='<u8').reshape(nb, 4)
        with np.errstate(over='ignore'):
            sh = (w[:, 0] * np.uint64(0x9E3779B97F4A7C15) ^ w[:, 1] * np.uint64(0xC2B2AE3D27D4EB4F) ^
                  w[:, 2] * np.uint64(0x165667B19E3779F9) ^ w[:, 3] * np.uint64(0x27D4EB2F165667C5))
        order = np.argsort(sh, kind='stable'); shs = sh[order]; soff = order.astype(np.int64) * BLOCK
        for g0, g1 in gaps:
            if g1 - g0 < MINCOPY:
                ops.append(('A', g0, g0, g1)); continue
            th = _win_hash(T[g0:g1], g1 - g0 - BLOCK + 1)
            ii = np.searchsorted(shs, th)
            ii[ii >= len(shs)] = len(shs) - 1
            hit = np.flatnonzero(shs[ii] == th)
            p = g0; lit = g0
            for h in hit:
                tp = g0 + int(h)
                if tp < p: continue
                so = int(soff[ii[h]])
                if not np.array_equal(S[so:so + BLOCK], T[tp:tp + BLOCK]): continue
                # extend forward
                e = BLOCK; step = 4096
                while tp + e < g1 and so + e < len(S):
                    n = min(step, g1 - tp - e, len(S) - so - e)
                    seg = S[so + e:so + e + n] == T[tp + e:tp + e + n]
                    if seg.all(): e += n; continue
                    e += int(np.argmin(seg)); break
                # extend backward
                b = 0
                while tp - b - 1 >= lit and so - b - 1 >= 0 and S[so - b - 1] == T[tp - b - 1]: b += 1
                if e + b < MINCOPY: continue
                if tp - b > lit: ops.append(('A', lit, lit, tp - b))
                ops.append(('C', tp - b, so - b, e + b))
                p = lit = tp + e
            if lit < g1: ops.append(('A', lit, lit, g1))
    ops.sort(key=lambda o: o[1])
    # serialise
    body = bytearray(); covered = 0
    for o in ops:
        tstart = o[1]
        assert tstart == covered, (tstart, covered)
        if o[0] == 'C':
            body += struct.pack('<BQI', 1, o[2], o[3]); covered += o[3]
        else:
            a, b = o[2], o[3]
            for c in range(a, b, 1 << 24):
                d = min(b, c + (1 << 24)); body += struct.pack('<BI', 2, d - c) + tgt[c:d]
            covered = b
    assert covered == len(T), (covered, len(T))
    body += b'\0'
    head = MAGIC + struct.pack('<Q', len(src)) + hashlib.sha256(src).digest() + struct.pack('<Q', len(tgt)) + hashlib.sha256(tgt).digest()
    return head + zlib.compress(bytes(body), 9)

def apply_delta(src, patch):
    assert patch[:6] == MAGIC
    sl, = struct.unpack_from('<Q', patch, 6); ssha = patch[14:46]
    tl, = struct.unpack_from('<Q', patch, 46); tsha = patch[54:86]
    assert len(src) == sl and hashlib.sha256(src).digest() == ssha, 'source mismatch'
    body = zlib.decompress(patch[86:]); out = bytearray(); i = 0
    while True:
        op = body[i]; i += 1
        if op == 0: break
        if op == 1:
            off, n = struct.unpack_from('<QI', body, i); i += 12; out += src[off:off + n]
        elif op == 2:
            n, = struct.unpack_from('<I', body, i); i += 4; out += body[i:i + n]; i += n
        else: raise ValueError(op)
    assert len(out) == tl and hashlib.sha256(out).digest() == tsha, 'target mismatch'
    return bytes(out)

if __name__ == '__main__':
    s = open(sys.argv[1], 'rb').read(); t = open(sys.argv[2], 'rb').read()
    p = make_delta(s, t); open(sys.argv[3], 'wb').write(p)
    assert apply_delta(s, p) == t
    print(sys.argv[3], len(t), '->', len(p))
