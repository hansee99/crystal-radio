# Fits a logistic-regression music/non-music classifier on the CSV(s) that DjDetector writes,
# then prints the coefficients folded into raw-feature space (ready to paste into
# MusicDetector.cs). Re-run when the labelled corpus grows.
#
#   1) dotnet run --project tools/DjDetector -- C:\clips   # clips under music/ and talk|ads/…
#   2) python tools/DjDetector/fit_logreg.py djdetector-features.csv [more-features.csv ...]
#   3) paste the printed LrBias / Lr_* constants into MusicDetector.cs
#
# Accepts multiple CSVs so the original labelled-clips corpus and a --corrections run over real
# harvested songs can be combined in one fit without manually merging files first. Filenames are
# assumed unique across all inputs (used for the file-grouped train/test split below).
#
# Needs numpy (no sklearn). Parses each CSV right-anchored because some filenames contain commas.
import sys, numpy as np

# Columns (right-anchored, because some filenames contain commas and the CSV is unquoted):
# ... file(commas) , label , tStart , mod4Hz , zcrMean , zcrVar , lowEnergyRatio ,
#     fluxMean , fluxVar , centroidMean , centroidVar , rolloffMean , flatness , confidence
FEAT_NAMES = ["mod4Hz","zcrMean","zcrVar","lowEnergyRatio","fluxMean","fluxVar",
              "centroidMean","centroidVar","rolloffMean","flatness"]

paths = sys.argv[1:]
if not paths:
    sys.exit("usage: fit_logreg.py <features.csv> [more.csv ...]")

X, y, groups = [], [], []
for path in paths:
    with open(path, encoding="utf-8") as f:
        next(f)  # header
        for line in f:
            p = line.rstrip("\n").split(",")
            if len(p) < 14: continue
            label = p[-13]
            if label not in ("music","nonmusic"): continue
            try:
                feats = [float(p[i]) for i in (-11,-10,-9,-8,-7,-6,-5,-4,-3,-2)]
            except ValueError:
                continue
            X.append(feats); y.append(1.0 if label=="music" else 0.0)
            groups.append(",".join(p[:-13]))  # filename

X = np.array(X); y = np.array(y); groups = np.array(groups)
print(f"inputs={len(paths)}: {', '.join(paths)}")
print(f"rows={len(y)}  music={int(y.sum())}  nonmusic={int((1-y).sum())}  files={len(set(groups))}")

def sigmoid(z):
    # Numerically stable: avoids exp() overflow on large |z| (harmless with IEEE inf handling,
    # but the RuntimeWarning noise isn't worth ignoring once real fits push logits far from 0).
    out = np.empty_like(z, dtype=float)
    pos = z >= 0
    out[pos] = 1 / (1 + np.exp(-z[pos]))
    ez = np.exp(z[~pos])
    out[~pos] = ez / (1 + ez)
    return out

def class_weights(y):
    # Inverse-frequency weights so each class contributes equally to the fit regardless of how
    # many windows it has — otherwise a batch of new same-class data (e.g. adding a pile of
    # correctly-labelled "music" windows with no matching non-music growth) skews the decision
    # boundary toward the now-larger class, degrading the OTHER class's accuracy. Weights sum to
    # n_pos*w_pos = n_neg*w_neg = n/2 each, so total weight == n (same overall scale as
    # unweighted, so the existing lr/iters still apply).
    n = len(y); n_pos = y.sum(); n_neg = n - n_pos
    return np.where(y == 1, n / (2 * n_pos), n / (2 * n_neg))

def fit(Xtr, ytr, iters=4000, lr=0.3, l2=1e-3):
    mu = Xtr.mean(0); sd = Xtr.std(0); sd[sd==0]=1
    Z = (Xtr-mu)/sd
    w = np.zeros(Z.shape[1]); b = 0.0
    n = len(ytr)
    sw = class_weights(ytr)
    for _ in range(iters):
        p = sigmoid(Z@w+b)
        err = (p-ytr) * sw
        gw = Z.T@err/n + l2*w
        gb = err.mean()
        w -= lr*gw; b -= lr*gb
    return w, b, mu, sd

def acc(X_, y_, w, b, mu, sd):
    p = sigmoid(((X_-mu)/sd)@w+b)
    pred = (p>=0.5).astype(float)
    m = y_==1; nm = y_==0
    return (pred==y_).mean(), (pred[m]==1).mean(), (pred[nm]==0).mean()

# Honest, file-grouped K-FOLD cross-validation (not a single 80/20 split): with a modest file
# count, one random split can land the "hard" files disproportionately in train or test, making a
# single held-out number noisy/misleading (a 10+pt swing between two runs doesn't necessarily mean
# either model is actually better). Averaging over K folds — each file always in exactly one test
# fold — gives a much more trustworthy estimate of real-world (out-of-sample) accuracy.
K = 5
rng = np.random.default_rng(42)
files = np.array(sorted(set(groups)))
rng.shuffle(files)
folds = np.array_split(files, K)
fold_accs = []
for k in range(K):
    test_files = set(folds[k])
    te = np.array([g in test_files for g in groups]); tr = ~te
    w,b,mu,sd = fit(X[tr], y[tr])
    fold_accs.append(acc(X[te], y[te], w,b,mu,sd))
fold_accs = np.array(fold_accs)
mean, std = fold_accs.mean(0), fold_accs.std(0)
print(f"{K}-FOLD CV (mean±std over folds, file-grouped):")
print(f"  overall={mean[0]*100:.1f}%±{std[0]*100:.1f}  music={mean[1]*100:.1f}%±{std[1]*100:.1f}  "
      f"nonmusic={mean[2]*100:.1f}%±{std[2]*100:.1f}")
for k in range(K):
    o,m,nm = fold_accs[k]
    print(f"  fold {k+1}: overall={o*100:5.1f}%  music={m*100:5.1f}%  nonmusic={nm*100:5.1f}%")

# Final model on ALL data → fold standardization into raw-feature weights for the C# detector.
w,b,mu,sd = fit(X, y)
oa,ma,nma = acc(X, y, w,b,mu,sd)
print(f"ALL-DATA fit:             overall={oa*100:.1f}%  music={ma*100:.1f}%  nonmusic={nma*100:.1f}%")
raw_w = w/sd
raw_b = b - float(np.sum(w*mu/sd))
print("\n// music_confidence = sigmoid(bias + sum(w_i * feature_i))")
print(f"private const double LrBias = {raw_b:.8g};")
for name, wi in zip(FEAT_NAMES, raw_w):
    print(f"private const double Lr_{name} = {wi:.8g};")
