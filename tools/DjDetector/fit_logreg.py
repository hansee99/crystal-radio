# Fits a logistic-regression music/non-music classifier on the CSV that DjDetector writes,
# then prints the coefficients folded into raw-feature space (ready to paste into
# MusicDetector.cs). Re-run when the labelled corpus grows.
#
#   1) dotnet run --project tools/DjDetector -- C:\clips   # clips under music/ and talk|ads/…
#   2) python tools/DjDetector/fit_logreg.py djdetector-features.csv
#   3) paste the printed LrBias / Lr_* constants into MusicDetector.cs
#
# Needs numpy (no sklearn). Parses the CSV right-anchored because some filenames contain commas.
import sys, numpy as np

# Columns (right-anchored, because some filenames contain commas and the CSV is unquoted):
# ... file(commas) , label , tStart , mod4Hz , zcrMean , zcrVar , lowEnergyRatio ,
#     fluxMean , fluxVar , centroidMean , centroidVar , rolloffMean , flatness , confidence
FEAT_NAMES = ["mod4Hz","zcrMean","zcrVar","lowEnergyRatio","fluxMean","fluxVar",
              "centroidMean","centroidVar","rolloffMean","flatness"]

path = sys.argv[1]
X, y, groups = [], [], []
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
print(f"rows={len(y)}  music={int(y.sum())}  nonmusic={int((1-y).sum())}  files={len(set(groups))}")

def fit(Xtr, ytr, iters=4000, lr=0.3, l2=1e-3):
    mu = Xtr.mean(0); sd = Xtr.std(0); sd[sd==0]=1
    Z = (Xtr-mu)/sd
    w = np.zeros(Z.shape[1]); b = 0.0
    n = len(ytr)
    for _ in range(iters):
        p = 1/(1+np.exp(-(Z@w+b)))
        gw = Z.T@(p-ytr)/n + l2*w
        gb = (p-ytr).mean()
        w -= lr*gw; b -= lr*gb
    return w, b, mu, sd

def acc(X_, y_, w, b, mu, sd):
    p = 1/(1+np.exp(-(((X_-mu)/sd)@w+b)))
    pred = (p>=0.5).astype(float)
    m = y_==1; nm = y_==0
    return (pred==y_).mean(), (pred[m]==1).mean(), (pred[nm]==0).mean()

# Honest, file-grouped 80/20 split so correlated windows from one clip don't leak.
rng = np.random.default_rng(42)
files = np.array(sorted(set(groups)))
rng.shuffle(files)
cut = int(len(files)*0.8)
train_files = set(files[:cut]);
tr = np.array([g in train_files for g in groups]); te = ~tr
w,b,mu,sd = fit(X[tr], y[tr])
o,m,nm = acc(X[te], y[te], w,b,mu,sd)
print(f"HELD-OUT (20% of files): overall={o*100:.1f}%  music={m*100:.1f}%  nonmusic={nm*100:.1f}%")
o2,m2,nm2 = acc(X[tr], y[tr], w,b,mu,sd)
print(f"train:                    overall={o2*100:.1f}%  music={m2*100:.1f}%  nonmusic={nm2*100:.1f}%")

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
