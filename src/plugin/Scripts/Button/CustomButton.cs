using System;

namespace OmniBonk.Scripts.Button
{
    /// <summary>Uses a System.Action: Unity Actions are broken under BepInEx IL2CPP.</summary>
    public class CustomButton : MyButtonNormal
    {
        private Action onClickAction;
        private Action onStartHover;
        private Action onEndHover;

        public void SetOnClickAction(Action action)
        {
            onClickAction = action;
        }

        public void OverrideStartHoverAction(Action action)
        {
            onStartHover = action;
        }

        public void OverrideEndHoverAction(Action action)
        {
            onEndHover = action;
        }

        public override void OnClick()
        {
            onClickAction?.Invoke();
        }

        public override void StartHover()
        {
            onStartHover?.Invoke();
        }

        public override void StopHover()
        {
            onEndHover?.Invoke();
        }

    }
}
