package app.pocketmic;

import android.content.Context;
import android.graphics.*;
import android.util.AttributeSet;
import android.view.View;

public final class LevelView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final float[] values = new float[31];
    public LevelView(Context context, AttributeSet attrs) { super(context, attrs); }
    public void update(float peak) {
        System.arraycopy(values, 1, values, 0, values.length - 1); values[values.length - 1] = peak; invalidate();
    }
    @Override protected void onDraw(Canvas canvas) {
        float cx = getWidth() / 2f, cy = getHeight() / 2f, radius = Math.min(getWidth(), getHeight()) * .38f;
        paint.setColor(Color.rgb(232, 239, 224)); canvas.drawCircle(cx, cy, radius * 1.16f, paint);
        paint.setColor(Color.rgb(220, 233, 208)); canvas.drawCircle(cx, cy, radius, paint);
        float step = radius * 1.65f / values.length;
        paint.setColor(Color.rgb(57, 91, 54));
        for (int i = 0; i < values.length; i++) {
            float height = Math.max(5, values[i] * radius * 1.4f);
            float x = cx - radius * .825f + i * step;
            canvas.drawRoundRect(x, cy - height / 2, x + Math.max(2, step - 3), cy + height / 2, 3, 3, paint);
        }
    }
}
